namespace Ledgerly.Domain;

// Events are the source of truth: an account's state is rebuilt by replaying them in order.
// They are immutable facts in the past tense and are never updated or deleted.

public sealed record AccountOpened(Guid AccountId, string Owner, string Currency, decimal OverdraftLimit, DateTimeOffset OpenedAt);

public sealed record MoneyDeposited(Guid AccountId, decimal Amount, string Reference, DateTimeOffset At);

public sealed record MoneyWithdrawn(Guid AccountId, decimal Amount, string Reference, DateTimeOffset At);

/// <summary>Debit side of a transfer, recorded on the sender's stream.</summary>
public sealed record TransferSent(Guid AccountId, Guid TransferId, Guid ToAccountId, decimal Amount, string Reference, DateTimeOffset At);

/// <summary>Credit side of a transfer, recorded on the receiver's stream.</summary>
public sealed record TransferReceived(Guid AccountId, Guid TransferId, Guid FromAccountId, decimal Amount, string Reference, DateTimeOffset At);

public sealed record AccountFrozen(Guid AccountId, string Reason, DateTimeOffset At);

public sealed record AccountUnfrozen(Guid AccountId, DateTimeOffset At);

public sealed record AccountClosed(Guid AccountId, DateTimeOffset At);
