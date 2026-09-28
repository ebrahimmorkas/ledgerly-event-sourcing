using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Ledgerly.Api.Grpc;

namespace Ledgerly.Api.Tests;

[Collection(LedgerCollection.Name)]
public sealed class LedgerTests(LedgerApiFactory factory)
{
    private const string SkipReason = "Integration tests need Docker; set RUN_INTEGRATION=true to run them.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Ledger.LedgerClient Ledger => factory.Grpc;

    private async Task<string> OpenAsync(string deposit = "0", string currency = "EUR", string overdraft = "0")
    {
        var account = await Ledger.OpenAccountAsync(
            new OpenAccountRequest { Owner = "Test Owner", Currency = currency, OverdraftLimit = overdraft }, cancellationToken: Ct);

        if (deposit != "0")
        {
            await Ledger.DepositAsync(new MoneyRequest { AccountId = account.AccountId, Amount = deposit, Reference = "initial" }, cancellationToken: Ct);
        }

        return account.AccountId;
    }

    private async Task<string> BalanceAsync(string id) =>
        (await Ledger.GetAccountAsync(new GetAccountRequest { AccountId = id }, cancellationToken: Ct)).Balance;

    [Fact]
    public async Task Deposit_And_Withdraw_Should_Update_Balance()
    {
        Assert.SkipUnless(LedgerApiFactory.IsEnabled, SkipReason);
        var id = await OpenAsync(deposit: "100.00");

        var reply = await Ledger.WithdrawAsync(new MoneyRequest { AccountId = id, Amount = "30.25", Reference = "atm" }, cancellationToken: Ct);

        reply.Balance.ShouldBe("69.75");
        reply.Version.ShouldBe(3);
    }

    [Fact]
    public async Task Transfer_Should_Move_Money_Atomically_Between_Accounts()
    {
        Assert.SkipUnless(LedgerApiFactory.IsEnabled, SkipReason);
        var from = await OpenAsync(deposit: "200");
        var to = await OpenAsync(deposit: "50");

        var transfer = await Ledger.TransferAsync(new TransferRequest
        {
            IdempotencyKey = Guid.NewGuid().ToString(), FromAccountId = from, ToAccountId = to, Amount = "75.50", Reference = "invoice 7"
        }, cancellationToken: Ct);

        transfer.Replayed.ShouldBeFalse();
        (await BalanceAsync(from)).ShouldBe("124.50");
        (await BalanceAsync(to)).ShouldBe("125.50");
    }

    [Fact]
    public async Task Retried_Transfer_With_Same_Key_Should_Not_Move_Money_Twice()
    {
        Assert.SkipUnless(LedgerApiFactory.IsEnabled, SkipReason);
        var from = await OpenAsync(deposit: "100");
        var to = await OpenAsync();
        var request = new TransferRequest
        {
            IdempotencyKey = $"order-{Guid.NewGuid()}", FromAccountId = from, ToAccountId = to, Amount = "40", Reference = "order payment"
        };

        var first = await Ledger.TransferAsync(request, cancellationToken: Ct);
        var retry = await Ledger.TransferAsync(request, cancellationToken: Ct);

        retry.Replayed.ShouldBeTrue();
        retry.TransferId.ShouldBe(first.TransferId);
        (await BalanceAsync(from)).ShouldBe("60.00");

        request.Amount = "41";
        var misuse = await Should.ThrowAsync<RpcException>(() => Ledger.TransferAsync(request, cancellationToken: Ct).ResponseAsync);
        misuse.StatusCode.ShouldBe(StatusCode.Aborted);
    }

    [Fact]
    public async Task Concurrent_Withdrawals_Should_Never_Overdraw()
    {
        Assert.SkipUnless(LedgerApiFactory.IsEnabled, SkipReason);
        var id = await OpenAsync(deposit: "100");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            try
            {
                await Ledger.WithdrawAsync(new MoneyRequest { AccountId = id, Amount = "20", Reference = $"w{i}" }, cancellationToken: Ct);
                return true;
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.FailedPrecondition or StatusCode.Aborted)
            {
                return false;
            }
        }));

        var balance = decimal.Parse(await BalanceAsync(id), System.Globalization.CultureInfo.InvariantCulture);
        attempts.Count(ok => ok).ShouldBeLessThanOrEqualTo(5);
        balance.ShouldBe(100 - attempts.Count(ok => ok) * 20);
        balance.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Business_Rules_Should_Map_To_Grpc_Status_Codes()
    {
        Assert.SkipUnless(LedgerApiFactory.IsEnabled, SkipReason);
        var id = await OpenAsync(deposit: "10");

        async Task<StatusCode> StatusOf(Func<Task> call) => (await Should.ThrowAsync<RpcException>(call)).StatusCode;

        (await StatusOf(() => Ledger.WithdrawAsync(new MoneyRequest { AccountId = id, Amount = "50", Reference = "too much" }, cancellationToken: Ct).ResponseAsync))
            .ShouldBe(StatusCode.FailedPrecondition);
        (await StatusOf(() => Ledger.DepositAsync(new MoneyRequest { AccountId = id, Amount = "abc", Reference = "x" }, cancellationToken: Ct).ResponseAsync))
            .ShouldBe(StatusCode.InvalidArgument);
        (await StatusOf(() => Ledger.GetAccountAsync(new GetAccountRequest { AccountId = Guid.NewGuid().ToString() }, cancellationToken: Ct).ResponseAsync))
            .ShouldBe(StatusCode.NotFound);

        await Ledger.FreezeAccountAsync(new FreezeAccountRequest { AccountId = id, Reason = "fraud review" }, cancellationToken: Ct);
        (await StatusOf(() => Ledger.WithdrawAsync(new MoneyRequest { AccountId = id, Amount = "1", Reference = "blocked" }, cancellationToken: Ct).ResponseAsync))
            .ShouldBe(StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Statement_Should_Show_Every_Movement_With_Running_Balance()
    {
        Assert.SkipUnless(LedgerApiFactory.IsEnabled, SkipReason);
        var id = await OpenAsync(deposit: "100");
        var other = await OpenAsync();
        await Ledger.WithdrawAsync(new MoneyRequest { AccountId = id, Amount = "10", Reference = "coffee" }, cancellationToken: Ct);
        await Ledger.TransferAsync(new TransferRequest
        {
            IdempotencyKey = Guid.NewGuid().ToString(), FromAccountId = id, ToAccountId = other, Amount = "40", Reference = "rent"
        }, cancellationToken: Ct);

        var statement = await Ledger.GetStatementAsync(new StatementRequest { AccountId = id }, cancellationToken: Ct);

        statement.Entries.Select(e => e.Type).ShouldBe(["Deposit", "Withdrawal", "TransferOut"]);
        statement.Entries.Select(e => e.BalanceAfter).ShouldBe(["100.00", "90.00", "50.00"]);
        statement.Entries[2].CounterpartyAccountId.ShouldBe(other);
        statement.ClosingBalance.ShouldBe("50.00");
    }

    [Fact]
    public async Task Balance_At_A_Past_Moment_Should_Ignore_Later_Events()
    {
        Assert.SkipUnless(LedgerApiFactory.IsEnabled, SkipReason);
        var id = await OpenAsync(deposit: "100");

        await Task.Delay(1500, Ct);
        var checkpoint = DateTimeOffset.UtcNow;
        await Task.Delay(1500, Ct);

        await Ledger.DepositAsync(new MoneyRequest { AccountId = id, Amount = "250", Reference = "bonus" }, cancellationToken: Ct);

        var past = await Ledger.GetBalanceAtAsync(new BalanceAtRequest { AccountId = id, AsOf = checkpoint.ToString("O") }, cancellationToken: Ct);
        var now = await Ledger.GetBalanceAtAsync(new BalanceAtRequest { AccountId = id, AsOf = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O") }, cancellationToken: Ct);

        past.Balance.ShouldBe("100.00");
        now.Balance.ShouldBe("350.00");
    }

    [Fact]
    public async Task Rest_Endpoints_Should_Be_Available_Through_Json_Transcoding()
    {
        Assert.SkipUnless(LedgerApiFactory.IsEnabled, SkipReason);

        var created = await factory.Rest.PostAsJsonAsync("/v1/accounts", new { owner = "REST User", currency = "usd" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var account = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = account.GetProperty("accountId").GetString();
        account.GetProperty("currency").GetString().ShouldBe("USD");

        var deposit = await factory.Rest.PostAsJsonAsync($"/v1/accounts/{id}/deposits", new { amount = "12.34", reference = "cash" }, Ct);
        (await deposit.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("balance").GetString().ShouldBe("12.34");

        var invalid = await factory.Rest.PostAsJsonAsync($"/v1/accounts/{id}/deposits", new { amount = "-5", reference = "bad" }, Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
