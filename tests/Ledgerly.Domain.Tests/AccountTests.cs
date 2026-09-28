using Ledgerly.Domain;

namespace Ledgerly.Domain.Tests;

/// <summary>Given past events, when a command runs, then these events (or this error) result.</summary>
public class AccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid AccountId = Guid.NewGuid();

    private static Account Given(params object[] events) => Account.Replay(events);

    private static AccountOpened Opened(Guid? id = null, string currency = "EUR", decimal overdraft = 0) =>
        new(id ?? AccountId, "Jane Doe", currency, overdraft, Now);

    private static MoneyDeposited Deposited(decimal amount, Guid? id = null) => new(id ?? AccountId, amount, "salary", Now);

    [Fact]
    public void Open_Should_Normalize_Input()
    {
        var opened = Account.Open(AccountId, "  Jane Doe ", "eur", 100, Now).Value;

        opened.Owner.ShouldBe("Jane Doe");
        opened.Currency.ShouldBe("EUR");
    }

    [Theory]
    [InlineData("", "EUR", 0, "Account.InvalidOwner")]
    [InlineData("Jane", "EURO", 0, "Account.InvalidCurrency")]
    [InlineData("Jane", "E1R", 0, "Account.InvalidCurrency")]
    [InlineData("Jane", "EUR", -1, "Account.InvalidOverdraft")]
    public void Open_Should_Validate(string owner, string currency, decimal overdraft, string expected)
    {
        Account.Open(AccountId, owner, currency, overdraft, Now).Error!.Code.ShouldBe(expected);
    }

    [Fact]
    public void Replay_Should_Rebuild_Balance_From_History()
    {
        var account = Given(
            Opened(),
            Deposited(100),
            new MoneyWithdrawn(AccountId, 30, "atm", Now),
            new TransferReceived(AccountId, Guid.NewGuid(), Guid.NewGuid(), 50, "rent share", Now),
            new TransferSent(AccountId, Guid.NewGuid(), Guid.NewGuid(), 20, "gift", Now));

        account.Balance.ShouldBe(100m);
        account.Status.ShouldBe(AccountStatus.Active);
    }

    [Theory]
    [InlineData(0, "Money.InvalidAmount")]
    [InlineData(-5, "Money.InvalidAmount")]
    [InlineData(10.001, "Money.TooManyDecimals")]
    public void Deposit_Should_Validate_Amount(decimal amount, string expected)
    {
        Given(Opened()).Deposit(amount, "ref", Now).Error!.Code.ShouldBe(expected);
    }

    [Fact]
    public void Deposit_Should_Require_A_Reference()
    {
        Given(Opened()).Deposit(10, "   ", Now).Error.ShouldBe(AccountErrors.InvalidReference);
    }

    [Fact]
    public void Withdraw_Should_Allow_Overdraft_Up_To_Limit()
    {
        var account = Given(Opened(overdraft: 50), Deposited(100));

        account.Withdraw(150, "rent", Now).IsSuccess.ShouldBeTrue();
        account.Withdraw(150.01m, "rent", Now).Error!.Code.ShouldBe("Account.InsufficientFunds");
    }

    [Fact]
    public void Frozen_Account_Should_Accept_Deposits_But_Block_Debits()
    {
        var account = Given(Opened(), Deposited(100), new AccountFrozen(AccountId, "fraud check", Now));

        account.Deposit(10, "refund", Now).IsSuccess.ShouldBeTrue();
        account.Withdraw(10, "atm", Now).Error!.Code.ShouldBe("Account.Frozen");
    }

    [Fact]
    public void Freeze_And_Unfreeze_Should_Follow_State()
    {
        var active = Given(Opened());
        active.Unfreeze(Now).Error.ShouldBe(AccountErrors.NotFrozen);
        active.Freeze("", Now).Error.ShouldBe(AccountErrors.ReasonRequired);

        var frozen = Given(Opened(), new AccountFrozen(AccountId, "kyc", Now));
        frozen.Freeze("again", Now).Error.ShouldBe(AccountErrors.AlreadyFrozen);
        frozen.Unfreeze(Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Close_Should_Require_Zero_Balance_And_Then_Block_Everything()
    {
        Given(Opened(), Deposited(1)).Close(Now).Error!.Code.ShouldBe("Account.BalanceNotZero");

        var closed = Given(Opened(), new AccountClosed(AccountId, Now));
        closed.Deposit(10, "late", Now).Error!.Code.ShouldBe("Account.Closed");
        closed.Close(Now).Error!.Code.ShouldBe("Account.Closed");
    }

    [Fact]
    public void Transfer_Should_Produce_Matching_Debit_And_Credit()
    {
        var receiverId = Guid.NewGuid();
        var sender = Given(Opened(), Deposited(100));
        var receiver = Given(Opened(receiverId));
        var transferId = Guid.NewGuid();

        var (debit, credit) = sender.TransferTo(receiver, transferId, 40, " invoice 42 ", Now).Value;

        debit.ShouldBe(new TransferSent(AccountId, transferId, receiverId, 40, "invoice 42", Now));
        credit.ShouldBe(new TransferReceived(receiverId, transferId, AccountId, 40, "invoice 42", Now));
    }

    [Fact]
    public void Transfer_Should_Conserve_Money()
    {
        var receiverId = Guid.NewGuid();
        var sender = Given(Opened(), Deposited(100));
        var receiver = Given(Opened(receiverId), Deposited(10, receiverId));

        var (debit, credit) = sender.TransferTo(receiver, Guid.NewGuid(), 60, "split", Now).Value;
        sender.Apply(debit);
        receiver.Apply(credit);

        (sender.Balance + receiver.Balance).ShouldBe(110m);
        sender.Balance.ShouldBe(40m);
    }

    [Fact]
    public void Transfer_Should_Reject_Invalid_Combinations()
    {
        var sender = Given(Opened(), Deposited(100));

        sender.TransferTo(sender, Guid.NewGuid(), 10, "self", Now).Error.ShouldBe(AccountErrors.SameAccount);

        var usd = Given(Opened(Guid.NewGuid(), currency: "USD"));
        sender.TransferTo(usd, Guid.NewGuid(), 10, "fx", Now).Error!.Code.ShouldBe("Transfer.CurrencyMismatch");

        var closedId = Guid.NewGuid();
        var closed = Given(Opened(closedId), new AccountClosed(closedId, Now));
        sender.TransferTo(closed, Guid.NewGuid(), 10, "late", Now).Error!.Code.ShouldBe("Account.Closed");

        var receiver = Given(Opened(Guid.NewGuid()));
        sender.TransferTo(receiver, Guid.NewGuid(), 500, "too much", Now).Error!.Code.ShouldBe("Account.InsufficientFunds");
    }

    [Fact]
    public void Replay_Should_Require_Stream_To_Start_With_AccountOpened()
    {
        Should.Throw<InvalidOperationException>(() => Account.Replay([Deposited(10)]));
    }
}
