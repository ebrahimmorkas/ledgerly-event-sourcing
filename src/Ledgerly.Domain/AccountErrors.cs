namespace Ledgerly.Domain;

public static class AccountErrors
{
    public static readonly Error InvalidOwner = new("Account.InvalidOwner", "Owner name must be between 1 and 100 characters.", ErrorType.Validation);

    public static readonly Error InvalidCurrency = new("Account.InvalidCurrency", "Currency must be a 3-letter ISO 4217 code such as EUR.", ErrorType.Validation);

    public static readonly Error InvalidOverdraft = new("Account.InvalidOverdraft", "Overdraft limit cannot be negative.", ErrorType.Validation);

    public static readonly Error InvalidAmount = new("Money.InvalidAmount", "Amount must be greater than zero.", ErrorType.Validation);

    public static readonly Error TooManyDecimals = new("Money.TooManyDecimals", "Amount can have at most two decimal places.", ErrorType.Validation);

    public static readonly Error InvalidReference = new("Money.InvalidReference", $"Reference is required and can be at most {Account.MaxReferenceLength} characters.", ErrorType.Validation);

    public static readonly Error SameAccount = new("Transfer.SameAccount", "Cannot transfer money to the same account.", ErrorType.Validation);

    public static readonly Error AlreadyFrozen = new("Account.AlreadyFrozen", "The account is already frozen.", ErrorType.FailedPrecondition);

    public static readonly Error NotFrozen = new("Account.NotFrozen", "The account is not frozen.", ErrorType.FailedPrecondition);

    public static readonly Error ReasonRequired = new("Account.ReasonRequired", "A reason is required to freeze an account.", ErrorType.Validation);

    public static Error NotFound(Guid id) => new("Account.NotFound", $"Account '{id}' was not found.", ErrorType.NotFound);

    public static Error Closed(Guid id) => new("Account.Closed", $"Account '{id}' is closed.", ErrorType.FailedPrecondition);

    public static Error Frozen(Guid id) => new("Account.Frozen", $"Account '{id}' is frozen; debits are blocked.", ErrorType.FailedPrecondition);

    public static Error InsufficientFunds(decimal available, string currency) =>
        new("Account.InsufficientFunds", $"Insufficient funds: {available:0.00} {currency} available including overdraft.", ErrorType.FailedPrecondition);

    public static Error CurrencyMismatch(string from, string to) =>
        new("Transfer.CurrencyMismatch", $"Cannot transfer between {from} and {to} accounts.", ErrorType.Validation);

    public static Error BalanceNotZero(decimal balance, string currency) =>
        new("Account.BalanceNotZero", $"Only accounts with a zero balance can be closed (balance: {balance:0.00} {currency}).", ErrorType.FailedPrecondition);
}
