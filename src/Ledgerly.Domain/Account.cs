namespace Ledgerly.Domain;

public enum AccountStatus
{
    Active = 0,
    Frozen = 1,
    Closed = 2
}

/// <summary>
/// Event-sourced bank account.
/// <para>
/// Decision methods (<see cref="Deposit"/>, <see cref="Withdraw"/>, …) validate a command against the
/// current state and return the event(s) to record; they never mutate state. State only changes through
/// the <c>Apply</c> methods when events are replayed. This keeps business rules pure and easy to test:
/// given past events, when a command runs, then these events (or this error) result.
/// </para>
/// </summary>
public sealed class Account
{
    public const int MaxReferenceLength = 140;

    public Guid Id { get; private set; }

    public string Owner { get; private set; } = string.Empty;

    public string Currency { get; private set; } = string.Empty;

    public decimal Balance { get; private set; }

    public decimal OverdraftLimit { get; private set; }

    public AccountStatus Status { get; private set; }

    /// <summary>Stream version, set by the event store; used for optimistic concurrency.</summary>
    public long Version { get; set; }

    public decimal AvailableToSpend => Balance + OverdraftLimit;

    // ---------- decisions ----------

    public static Result<AccountOpened> Open(Guid accountId, string owner, string currency, decimal overdraftLimit, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Trim().Length > 100)
        {
            return AccountErrors.InvalidOwner;
        }

        if (currency is null || currency.Length != 3 || !currency.All(char.IsLetter))
        {
            return AccountErrors.InvalidCurrency;
        }

        if (overdraftLimit < 0)
        {
            return AccountErrors.InvalidOverdraft;
        }

        return new AccountOpened(accountId, owner.Trim(), currency.ToUpperInvariant(), overdraftLimit, now);
    }

    public Result<MoneyDeposited> Deposit(decimal amount, string reference, DateTimeOffset now)
    {
        if (ValidateAmount(amount, reference) is { } invalid)
        {
            return invalid;
        }

        if (Status == AccountStatus.Closed)
        {
            return AccountErrors.Closed(Id);
        }

        return new MoneyDeposited(Id, amount, reference.Trim(), now);
    }

    public Result<MoneyWithdrawn> Withdraw(decimal amount, string reference, DateTimeOffset now)
    {
        if ((ValidateAmount(amount, reference) ?? CanDebit(amount)) is { } error)
        {
            return error;
        }

        return new MoneyWithdrawn(Id, amount, reference.Trim(), now);
    }

    /// <summary>
    /// Double-entry transfer: returns the debit event for this account and the credit event for the
    /// receiver. Both must be committed in the same transaction so money is never created or lost.
    /// </summary>
    public Result<(TransferSent Debit, TransferReceived Credit)> TransferTo(
        Account receiver,
        Guid transferId,
        decimal amount,
        string reference,
        DateTimeOffset now)
    {
        if (receiver.Id == Id)
        {
            return AccountErrors.SameAccount;
        }

        if (receiver.Currency != Currency)
        {
            return AccountErrors.CurrencyMismatch(Currency, receiver.Currency);
        }

        if ((ValidateAmount(amount, reference) ?? CanDebit(amount)) is { } error)
        {
            return error;
        }

        if (receiver.Status == AccountStatus.Closed)
        {
            return AccountErrors.Closed(receiver.Id);
        }

        var cleanReference = reference.Trim();
        return (
            new TransferSent(Id, transferId, receiver.Id, amount, cleanReference, now),
            new TransferReceived(receiver.Id, transferId, Id, amount, cleanReference, now));
    }

    public Result<AccountFrozen> Freeze(string reason, DateTimeOffset now) => Status switch
    {
        AccountStatus.Closed => AccountErrors.Closed(Id),
        AccountStatus.Frozen => AccountErrors.AlreadyFrozen,
        _ when string.IsNullOrWhiteSpace(reason) => AccountErrors.ReasonRequired,
        _ => new AccountFrozen(Id, reason.Trim(), now)
    };

    public Result<AccountUnfrozen> Unfreeze(DateTimeOffset now) =>
        Status == AccountStatus.Frozen ? new AccountUnfrozen(Id, now) : AccountErrors.NotFrozen;

    public Result<AccountClosed> Close(DateTimeOffset now) => Status switch
    {
        AccountStatus.Closed => AccountErrors.Closed(Id),
        _ when Balance != 0 => AccountErrors.BalanceNotZero(Balance, Currency),
        _ => new AccountClosed(Id, now)
    };

    // ---------- state (event application) ----------

    public static Account Create(AccountOpened e) => new()
    {
        Id = e.AccountId,
        Owner = e.Owner,
        Currency = e.Currency,
        OverdraftLimit = e.OverdraftLimit,
        Status = AccountStatus.Active
    };

    public void Apply(MoneyDeposited e) => Balance += e.Amount;

    public void Apply(MoneyWithdrawn e) => Balance -= e.Amount;

    public void Apply(TransferSent e) => Balance -= e.Amount;

    public void Apply(TransferReceived e) => Balance += e.Amount;

    public void Apply(AccountFrozen e) => Status = AccountStatus.Frozen;

    public void Apply(AccountUnfrozen e) => Status = AccountStatus.Active;

    public void Apply(AccountClosed e) => Status = AccountStatus.Closed;

    /// <summary>Rebuilds an account from its history (the event store does the same thing).</summary>
    public static Account Replay(IEnumerable<object> events)
    {
        Account? account = null;
        foreach (var e in events)
        {
            account = e switch
            {
                AccountOpened opened => Create(opened),
                _ when account is null => throw new InvalidOperationException("A stream must start with AccountOpened."),
                _ => Evolve(account, e)
            };
        }

        return account ?? throw new InvalidOperationException("No events to replay.");
    }

    private static Account Evolve(Account account, object e)
    {
        switch (e)
        {
            case MoneyDeposited x: account.Apply(x); break;
            case MoneyWithdrawn x: account.Apply(x); break;
            case TransferSent x: account.Apply(x); break;
            case TransferReceived x: account.Apply(x); break;
            case AccountFrozen x: account.Apply(x); break;
            case AccountUnfrozen x: account.Apply(x); break;
            case AccountClosed x: account.Apply(x); break;
        }

        return account;
    }

    // ---------- rules ----------

    private static Error? ValidateAmount(decimal amount, string reference)
    {
        if (amount <= 0)
        {
            return AccountErrors.InvalidAmount;
        }

        if (decimal.Round(amount, 2) != amount)
        {
            return AccountErrors.TooManyDecimals;
        }

        return reference?.Trim().Length is null or 0 or > MaxReferenceLength ? AccountErrors.InvalidReference : null;
    }

    private Error? CanDebit(decimal amount) => Status switch
    {
        AccountStatus.Closed => AccountErrors.Closed(Id),
        AccountStatus.Frozen => AccountErrors.Frozen(Id),
        _ when amount > AvailableToSpend => AccountErrors.InsufficientFunds(AvailableToSpend, Currency),
        _ => null
    };
}
