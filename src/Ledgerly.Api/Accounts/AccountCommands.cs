using Ledgerly.Domain;
using Marten;
using JasperFx;

namespace Ledgerly.Api.Accounts;

/// <summary>
/// Application service: loads aggregates from the event store, asks them for a decision and appends the
/// resulting events. Marten's <c>FetchForWriting</c> tracks each stream's version, so a concurrent change
/// makes <c>SaveChangesAsync</c> fail instead of silently overwriting; those conflicts are retried.
/// </summary>
public sealed class AccountCommands(IDocumentStore store, TimeProvider timeProvider)
{
    private const int MaxAttempts = 3;

    public async Task<Result<AccountSummary>> OpenAsync(string owner, string currency, decimal overdraftLimit, CancellationToken ct)
    {
        var accountId = Guid.CreateVersion7();
        var decision = Account.Open(accountId, owner, currency, overdraftLimit, Now);
        if (decision.IsFailure)
        {
            return decision.Error!;
        }

        await using var session = store.LightweightSession();
        session.Events.StartStream<Account>(accountId, decision.Value);
        await session.SaveChangesAsync(ct);

        return (await session.LoadAsync<AccountSummary>(accountId, ct))!;
    }

    public Task<Result<AccountSummary>> DepositAsync(Guid accountId, decimal amount, string reference, CancellationToken ct) =>
        ExecuteAsync(accountId, account => Wrap(account.Deposit(amount, reference, Now)), ct);

    public Task<Result<AccountSummary>> WithdrawAsync(Guid accountId, decimal amount, string reference, CancellationToken ct) =>
        ExecuteAsync(accountId, account => Wrap(account.Withdraw(amount, reference, Now)), ct);

    public Task<Result<AccountSummary>> FreezeAsync(Guid accountId, string reason, CancellationToken ct) =>
        ExecuteAsync(accountId, account => Wrap(account.Freeze(reason, Now)), ct);

    public Task<Result<AccountSummary>> UnfreezeAsync(Guid accountId, CancellationToken ct) =>
        ExecuteAsync(accountId, account => Wrap(account.Unfreeze(Now)), ct);

    public Task<Result<AccountSummary>> CloseAsync(Guid accountId, CancellationToken ct) =>
        ExecuteAsync(accountId, account => Wrap(account.Close(Now)), ct);

    public async Task<Result<(TransferRecord Record, bool Replayed)>> TransferAsync(
        string idempotencyKey,
        Guid fromId,
        Guid toId,
        decimal amount,
        string reference,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            return TransferErrors.InvalidIdempotencyKey;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await using var session = store.LightweightSession();

            if (await session.LoadAsync<TransferRecord>(idempotencyKey, ct) is { } previous)
            {
                return Replay(previous, fromId, toId, amount, reference);
            }

            var from = await session.Events.FetchForWriting<Account>(fromId, ct);
            var to = await session.Events.FetchForWriting<Account>(toId, ct);
            if (from.Aggregate is null)
            {
                return AccountErrors.NotFound(fromId);
            }

            if (to.Aggregate is null)
            {
                return AccountErrors.NotFound(toId);
            }

            var transferId = Guid.CreateVersion7();
            var decision = from.Aggregate.TransferTo(to.Aggregate, transferId, amount, reference, Now);
            if (decision.IsFailure)
            {
                return decision.Error!;
            }

            var (debit, credit) = decision.Value;
            from.AppendOne(debit);
            to.AppendOne(credit);

            var record = new TransferRecord
            {
                Id = idempotencyKey,
                TransferId = transferId,
                FromAccountId = fromId,
                ToAccountId = toId,
                Amount = amount,
                Currency = from.Aggregate.Currency,
                Reference = debit.Reference,
                ExecutedAt = debit.At
            };
            session.Insert(record);

            try
            {
                // Debit, credit and idempotency record commit in ONE database transaction.
                await session.SaveChangesAsync(ct);
                return (record, false);
            }
            catch (ConcurrencyException)
            {
                // Another command changed one of the accounts; retry with fresh state.
            }
            catch (DocumentAlreadyExistsException)
            {
                // A concurrent request with the same idempotency key won; return its outcome.
                await using var readSession = store.QuerySession();
                var winner = await readSession.LoadAsync<TransferRecord>(idempotencyKey, ct);
                return Replay(winner!, fromId, toId, amount, reference);
            }
        }

        return TransferErrors.TooMuchContention;
    }

    private async Task<Result<AccountSummary>> ExecuteAsync(
        Guid accountId,
        Func<Account, Result<object>> decide,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await using var session = store.LightweightSession();

            var stream = await session.Events.FetchForWriting<Account>(accountId, ct);
            if (stream.Aggregate is null)
            {
                return AccountErrors.NotFound(accountId);
            }

            var decision = decide(stream.Aggregate);
            if (decision.IsFailure)
            {
                return decision.Error!;
            }

            stream.AppendOne(decision.Value);

            try
            {
                await session.SaveChangesAsync(ct);
                return (await session.LoadAsync<AccountSummary>(accountId, ct))!;
            }
            catch (ConcurrencyException)
            {
                // Retry against the latest version of the stream.
            }
        }

        return TransferErrors.TooMuchContention;
    }

    private static Result<(TransferRecord, bool)> Replay(TransferRecord previous, Guid fromId, Guid toId, decimal amount, string reference) =>
        previous.Matches(fromId, toId, amount, reference)
            ? (previous, true)
            : TransferErrors.IdempotencyKeyReused;

    private static Result<object> Wrap<T>(Result<T> result) where T : notnull =>
        result.IsSuccess ? result.Value : result.Error!;

    private DateTimeOffset Now => timeProvider.GetUtcNow();
}

public static class TransferErrors
{
    public static readonly Error InvalidIdempotencyKey =
        new("Transfer.InvalidIdempotencyKey", "An idempotency key (max 100 characters) is required for transfers.", ErrorType.Validation);

    public static readonly Error IdempotencyKeyReused =
        new("Transfer.IdempotencyKeyReused", "This idempotency key was already used for a different transfer.", ErrorType.Conflict);

    public static readonly Error TooMuchContention =
        new("Ledger.Contention", "The account is being updated concurrently; please retry.", ErrorType.Conflict);
}
