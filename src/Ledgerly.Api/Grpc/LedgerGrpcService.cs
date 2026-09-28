using System.Globalization;
using Grpc.Core;
using Ledgerly.Api.Accounts;
using Ledgerly.Domain;
using Marten;

namespace Ledgerly.Api.Grpc;

/// <summary>
/// gRPC endpoint (also exposed as REST through JSON transcoding). Translates protobuf messages to
/// commands and domain errors to gRPC status codes.
/// </summary>
public sealed class LedgerGrpcService(AccountCommands commands, IQuerySession query) : Ledger.LedgerBase
{
    public override async Task<AccountReply> OpenAccount(OpenAccountRequest request, ServerCallContext context)
    {
        var overdraft = string.IsNullOrWhiteSpace(request.OverdraftLimit) ? 0 : ParseAmount(request.OverdraftLimit, "overdraft_limit");
        return ToReply(await commands.OpenAsync(request.Owner, request.Currency, overdraft, context.CancellationToken));
    }

    public override async Task<AccountReply> GetAccount(GetAccountRequest request, ServerCallContext context)
    {
        var id = ParseId(request.AccountId, "account_id");
        var summary = await query.LoadAsync<AccountSummary>(id, context.CancellationToken);
        return summary is null ? throw ToRpcException(AccountErrors.NotFound(id)) : Map(summary);
    }

    public override async Task<AccountReply> Deposit(MoneyRequest request, ServerCallContext context) =>
        ToReply(await commands.DepositAsync(
            ParseId(request.AccountId, "account_id"), ParseAmount(request.Amount, "amount"), request.Reference, context.CancellationToken));

    public override async Task<AccountReply> Withdraw(MoneyRequest request, ServerCallContext context) =>
        ToReply(await commands.WithdrawAsync(
            ParseId(request.AccountId, "account_id"), ParseAmount(request.Amount, "amount"), request.Reference, context.CancellationToken));

    public override async Task<AccountReply> FreezeAccount(FreezeAccountRequest request, ServerCallContext context) =>
        ToReply(await commands.FreezeAsync(ParseId(request.AccountId, "account_id"), request.Reason, context.CancellationToken));

    public override async Task<AccountReply> UnfreezeAccount(GetAccountRequest request, ServerCallContext context) =>
        ToReply(await commands.UnfreezeAsync(ParseId(request.AccountId, "account_id"), context.CancellationToken));

    public override async Task<AccountReply> CloseAccount(GetAccountRequest request, ServerCallContext context) =>
        ToReply(await commands.CloseAsync(ParseId(request.AccountId, "account_id"), context.CancellationToken));

    public override async Task<TransferReply> Transfer(TransferRequest request, ServerCallContext context)
    {
        var result = await commands.TransferAsync(
            request.IdempotencyKey,
            ParseId(request.FromAccountId, "from_account_id"),
            ParseId(request.ToAccountId, "to_account_id"),
            ParseAmount(request.Amount, "amount"),
            request.Reference,
            context.CancellationToken);

        if (result.IsFailure)
        {
            throw ToRpcException(result.Error!);
        }

        var (record, replayed) = result.Value;
        return new TransferReply
        {
            TransferId = record.TransferId.ToString(),
            FromAccountId = record.FromAccountId.ToString(),
            ToAccountId = record.ToAccountId.ToString(),
            Amount = Format(record.Amount),
            Currency = record.Currency,
            Reference = record.Reference,
            ExecutedAt = record.ExecutedAt.ToString("O", CultureInfo.InvariantCulture),
            Replayed = replayed
        };
    }

    internal static AccountReply Map(AccountSummary summary) => new()
    {
        AccountId = summary.Id.ToString(),
        Owner = summary.Owner,
        Currency = summary.Currency,
        Balance = Format(summary.Balance),
        OverdraftLimit = Format(summary.OverdraftLimit),
        Status = summary.Status.ToString(),
        Version = summary.Version
    };

    internal static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    internal static decimal ParseAmount(string value, string field) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{field}' must be a decimal string such as \"125.50\"."));

    internal static Guid ParseId(string value, string field) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{field}' must be a UUID."));

    internal static RpcException ToRpcException(Error error) => new(new Status(
        error.Type switch
        {
            ErrorType.Validation => StatusCode.InvalidArgument,
            ErrorType.NotFound => StatusCode.NotFound,
            ErrorType.Conflict => StatusCode.Aborted,
            ErrorType.FailedPrecondition => StatusCode.FailedPrecondition,
            _ => StatusCode.Internal
        },
        $"{error.Code}: {error.Message}"));

    private static AccountReply ToReply(Result<AccountSummary> result) =>
        result.IsSuccess ? Map(result.Value) : throw ToRpcException(result.Error!);
}
