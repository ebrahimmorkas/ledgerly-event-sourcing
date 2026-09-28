using Ledgerly.Api.Accounts;
using Ledgerly.Domain;

namespace Ledgerly.Api.Tests;

public class StatementBuilderTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static List<RecordedEvent> History() =>
    [
        new(1, new AccountOpened(Id, "Jane", "EUR", 0, T0), T0),
        new(2, new MoneyDeposited(Id, 100, "salary", T0), T0.AddDays(1)),
        new(3, new AccountFrozen(Id, "check", T0), T0.AddDays(2)),
        new(4, new TransferSent(Id, Guid.NewGuid(), Other, 30, "rent", T0), T0.AddDays(3)),
        new(5, new TransferReceived(Id, Guid.NewGuid(), Other, 5, "refund", T0), T0.AddDays(4)),
        new(6, new MoneyWithdrawn(Id, 25, "atm", T0), T0.AddDays(5))
    ];

    [Fact]
    public void Should_List_Money_Movements_With_Running_Balance()
    {
        var statement = StatementBuilder.Build(History());

        statement.Lines.Select(l => l.Type).ShouldBe(["Deposit", "TransferOut", "TransferIn", "Withdrawal"]);
        statement.Lines.Select(l => l.BalanceAfter).ShouldBe([100m, 70m, 75m, 50m]);
        statement.Lines[1].Counterparty.ShouldBe(Other);
        statement.OpeningBalance.ShouldBe(0m);
        statement.ClosingBalance.ShouldBe(50m);
        statement.Currency.ShouldBe("EUR");
    }

    [Fact]
    public void Window_Should_Carry_Opening_Balance_From_Earlier_History()
    {
        var statement = StatementBuilder.Build(History(), from: T0.AddDays(3), to: T0.AddDays(4));

        statement.OpeningBalance.ShouldBe(100m);
        statement.Lines.Select(l => l.Amount).ShouldBe([-30m, 5m]);
        statement.ClosingBalance.ShouldBe(75m);
    }

    [Fact]
    public void Account_Without_Movements_Should_Have_Empty_Statement()
    {
        var statement = StatementBuilder.Build([new RecordedEvent(1, new AccountOpened(Id, "Jane", "USD", 0, T0), T0)]);

        statement.Lines.ShouldBeEmpty();
        statement.ClosingBalance.ShouldBe(0m);
    }
}
