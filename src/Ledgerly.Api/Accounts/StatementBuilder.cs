using Ledgerly.Domain;

namespace Ledgerly.Api.Accounts;

public sealed record RecordedEvent(long Version, object Data, DateTimeOffset Timestamp);

public sealed record StatementLine(
    long Version,
    string Type,
    decimal Amount,
    decimal BalanceAfter,
    string Reference,
    Guid? Counterparty,
    DateTimeOffset OccurredAt);

public sealed record Statement(string Currency, decimal OpeningBalance, decimal ClosingBalance, IReadOnlyList<StatementLine> Lines);

/// <summary>
/// Builds a bank statement from an account's event stream. Pure function: the running balance is
/// computed from the full history, then lines are filtered to the requested window, so the opening
/// balance is correct even when the window starts mid-history.
/// </summary>
public static class StatementBuilder
{
    public static Statement Build(IReadOnlyList<RecordedEvent> events, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var currency = events.Select(e => e.Data).OfType<AccountOpened>().Single().Currency;
        var balance = 0m;
        var opening = 0m;
        var lines = new List<StatementLine>();

        foreach (var recorded in events)
        {
            var (type, amount, reference, counterparty) = recorded.Data switch
            {
                MoneyDeposited e => ("Deposit", e.Amount, e.Reference, (Guid?)null),
                MoneyWithdrawn e => ("Withdrawal", -e.Amount, e.Reference, null),
                TransferReceived e => ("TransferIn", e.Amount, e.Reference, e.FromAccountId),
                TransferSent e => ("TransferOut", -e.Amount, e.Reference, e.ToAccountId),
                _ => (null, 0m, string.Empty, null)
            };

            if (type is null)
            {
                continue; // lifecycle events (opened, frozen, …) don't move money
            }

            if (from is not null && recorded.Timestamp < from)
            {
                balance += amount;
                opening = balance;
                continue;
            }

            if (to is not null && recorded.Timestamp > to)
            {
                break;
            }

            balance += amount;
            lines.Add(new StatementLine(recorded.Version, type, amount, balance, reference, counterparty, recorded.Timestamp));
        }

        return new Statement(currency, opening, balance, lines);
    }
}
