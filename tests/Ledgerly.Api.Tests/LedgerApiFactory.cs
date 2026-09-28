using Grpc.Net.Client;
using Ledgerly.Api.Grpc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Ledgerly.Api.Tests;

/// <summary>
/// Runs the real service in memory against a throwaway PostgreSQL container (the Marten event store).
/// Requires Docker, so it runs in CI or when RUN_INTEGRATION=true.
/// </summary>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Created lazily so skipped runs never touch Docker.
    private PostgreSqlContainer? _database;

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("RUN_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase);

    public Ledger.LedgerClient Grpc { get; private set; } = null!;

    public HttpClient Rest { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Ledger", _database?.GetConnectionString() ?? "Host=unused");

    public async ValueTask InitializeAsync()
    {
        if (!IsEnabled)
        {
            return;
        }

        _database = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _database.StartAsync();

        var channel = GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });
        Grpc = new Ledger.LedgerClient(channel);
        Rest = CreateClient();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class LedgerCollection : ICollectionFixture<LedgerApiFactory>
{
    public const string Name = "Ledger";
}
