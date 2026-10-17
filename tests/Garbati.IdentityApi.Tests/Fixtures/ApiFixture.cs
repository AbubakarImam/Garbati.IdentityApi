using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace Garbati.IdentityApi.Tests.Fixtures;

// Runs the real app in memory (WebApplicationFactory) against its own Postgres container, so requests go
// through the full pipeline: correlation id, authentication, authorization, controllers and the database.
// One shared instance: Serilog's bootstrap logger can only be frozen once per process.
public class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    // For every scope there is a client holding only that scope, and one holding every scope except it.
    public static string OnlyClient(string scope) => $"only-{scope}";
    public static string AllButClient(string scope) => $"all-but-{scope}";
    public static string KeyFor(string clientId) => $"test-key-{clientId}";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using (var ctx = CreateContext())
        {
            await ctx.Database.MigrateAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:IdentityDb"] = _container.GetConnectionString(),
            ["Serilog:MinimumLevel:Default"] = "Warning",
            // The Testing environment is not Development, so this exercises the showcase switch.
            ["Swagger:Enabled"] = "true",
            // Test-only ES256 key (TestSigningKey) - never the dev key from appsettings.Development.json.
            ["SigningKeys:ActiveKid"] = TestSigningKey.Kid,
            ["SigningKeys:Keys:0:Kid"] = TestSigningKey.Kid,
            ["SigningKeys:Keys:0:PrivateKeyPem"] = TestSigningKey.PrivateKeyPem,
        };

        var clients = IdentityScopes.All
            .SelectMany(scope => new[]
            {
                (Id: OnlyClient(scope), Scopes: new[] { scope }),
                (Id: AllButClient(scope), Scopes: IdentityScopes.All.Where(s => s != scope).ToArray()),
            })
            .ToList();

        for (var i = 0; i < clients.Count; i++)
        {
            settings[$"ApiKeys:Clients:{i}:ClientId"] = clients[i].Id;
            settings[$"ApiKeys:Clients:{i}:KeyHash"] = Convert.ToBase64String(ApiKeyHasher.Hash(KeyFor(clients[i].Id)));
            for (var j = 0; j < clients[i].Scopes.Length; j++)
            {
                settings[$"ApiKeys:Clients:{i}:Scopes:{j}"] = clients[i].Scopes[j];
            }
        }

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        });
    }

    public IdentityDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new IdentityDbContext(options);
    }

    // An HTTP client for the in-memory app, sending the given client's key (or no key when null).
    public HttpClient CreateClient(string? clientId)
    {
        var client = Factory.CreateClient();
        if (clientId is not null)
        {
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, KeyFor(clientId));
        }
        return client;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition("Api")]
public class ApiCollection : ICollectionFixture<ApiFixture>;
