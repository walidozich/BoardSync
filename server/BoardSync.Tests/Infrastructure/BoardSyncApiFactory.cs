using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace BoardSync.Tests.Infrastructure;

public class BoardSyncApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("boardsync_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = _dbContainer.GetConnectionString(),
                    ["CorsOrigin"] = "http://localhost:5173",
                    ["Jwt:Secret"] = "test-only-signing-secret-not-used-outside-the-test-host-01234567",
                    ["Jwt:Issuer"] = "BoardSync.Tests",
                    ["Jwt:Audience"] = "BoardSync.Tests.Client",
                    ["Jwt:LifetimeHours"] = "8",
                    // High on purpose: this factory instance is shared (via ICollectionFixture)
                    // across every functional test case in the "Database" collection, all of
                    // which hit the same in-process partition key. A production-realistic limit
                    // would make those tests trip each other's budget as a side effect of
                    // unrelated assertions. AuthRateLimiterTests overrides this back down in its
                    // own isolated factory to actually exercise the limiter.
                    ["RateLimiting:Auth:PermitLimit"] = "1000",
                }
            );
        });
    }

    public Task InitializeAsync() => _dbContainer.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        await base.DisposeAsync();
    }
}
