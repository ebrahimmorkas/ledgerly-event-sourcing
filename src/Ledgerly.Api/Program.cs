using JasperFx;
using JasperFx.Events.Projections;
using Ledgerly.Api.Accounts;
using Ledgerly.Api.Grpc;
using Ledgerly.Domain;
using Marten;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Ledger")
    ?? throw new InvalidOperationException("Connection string 'Ledger' is not configured.");

builder.Services.AddMarten(options =>
    {
        options.Connection(connectionString);
        options.DatabaseSchemaName = "ledger";

        // Read model updated in the same transaction as the events it is built from.
        options.Projections.Add<AccountSummaryProjection>(ProjectionLifecycle.Inline);

        options.Schema.For<TransferRecord>().Identity(x => x.Id);
        options.Events.AddEventTypes([
            typeof(AccountOpened), typeof(MoneyDeposited), typeof(MoneyWithdrawn), typeof(TransferSent),
            typeof(TransferReceived), typeof(AccountFrozen), typeof(AccountUnfrozen), typeof(AccountClosed)
        ]);

        // Development convenience; in production, generate migrations with the Marten CLI instead.
        options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
    })
    .UseLightweightSessions()
    .ApplyAllDatabaseChangesOnStartup();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AccountCommands>();

builder.Services.AddGrpc(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment())
    .AddJsonTranscoding();
builder.Services.AddGrpcReflection();

var app = builder.Build();

app.MapGrpcService<LedgerGrpcService>();
app.MapGrpcReflectionService();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

await app.RunAsync();

public partial class Program;
