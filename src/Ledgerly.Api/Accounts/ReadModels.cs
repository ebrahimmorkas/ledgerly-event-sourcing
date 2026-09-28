using Ledgerly.Domain;
using Marten.Events.Aggregation;

namespace Ledgerly.Api.Accounts;

/// <summary>
/// Read model kept up to date in the same transaction as the events (inline projection), so reads
/// don't need to replay the stream.
/// </summary>
public sealed class AccountSummary
{
    public Guid Id { get; set; }

    public string Owner { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public decimal Balance { get; set; }

    public decimal OverdraftLimit { get; set; }

    public AccountStatus Status { get; set; }

    public int TransactionCount { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    public long Version { get; set; }
}

public sealed class AccountSummaryProjection : SingleStreamProjection<AccountSummary, Guid>
{
    public static AccountSummary Create(AccountOpened e) => new()
    {
        Id = e.AccountId,
        Owner = e.Owner,
        Currency = e.Currency,
        OverdraftLimit = e.OverdraftLimit,
        Status = AccountStatus.Active,
        OpenedAt = e.OpenedAt,
        LastActivityAt = e.OpenedAt
    };

    public static void Apply(MoneyDeposited e, AccountSummary view) => Move(view, e.Amount, e.At);

    public static void Apply(MoneyWithdrawn e, AccountSummary view) => Move(view, -e.Amount, e.At);

    public static void Apply(TransferReceived e, AccountSummary view) => Move(view, e.Amount, e.At);

    public static void Apply(TransferSent e, AccountSummary view) => Move(view, -e.Amount, e.At);

    public static void Apply(AccountFrozen e, AccountSummary view) => view.Status = AccountStatus.Frozen;

    public static void Apply(AccountUnfrozen e, AccountSummary view) => view.Status = AccountStatus.Active;

    public static void Apply(AccountClosed e, AccountSummary view) => view.Status = AccountStatus.Closed;

    private static void Move(AccountSummary view, decimal delta, DateTimeOffset at)
    {
        view.Balance += delta;
        view.TransactionCount++;
        view.LastActivityAt = at;
    }
}

/// <summary>
/// Stored alongside the two transfer events in the same transaction; its id is the client's
/// idempotency key, so a retried request finds the original outcome instead of moving money again.
/// </summary>
public sealed class TransferRecord
{
    public string Id { get; set; } = string.Empty;

    public Guid TransferId { get; set; }

    public Guid FromAccountId { get; set; }

    public Guid ToAccountId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string Reference { get; set; } = string.Empty;

    public DateTimeOffset ExecutedAt { get; set; }

    public bool Matches(Guid from, Guid to, decimal amount, string reference) =>
        FromAccountId == from && ToAccountId == to && Amount == amount && Reference == reference.Trim();
}
