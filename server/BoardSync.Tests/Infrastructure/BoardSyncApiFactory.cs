using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace BoardSync.Tests.Infrastructure;

public sealed class BoardSyncApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
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
