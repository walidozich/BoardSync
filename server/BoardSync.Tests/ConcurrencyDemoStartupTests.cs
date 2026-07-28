using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace BoardSync.Tests;

// Standalone on purpose: the guard under test runs in Program.cs before the database is ever
// touched (see the comment above the check), so this doesn't need the shared Postgres
// testcontainer BoardSyncApiFactory spins up for every other test in the collection.
public sealed class ConcurrencyDemoStartupTests
{
    private static Dictionary<string, string?> BaseConfig(string artificialDelayMs) =>
        new()
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["CorsOrigin"] = "http://localhost:5173",
            ["Jwt:Secret"] = "test-only-signing-secret-not-used-outside-the-test-host-01234567",
            ["Jwt:Issuer"] = "BoardSync.Tests",
            ["Jwt:Audience"] = "BoardSync.Tests.Client",
            ["ConcurrencyDemo:ArtificialDelayMs"] = artificialDelayMs,
        };

    [Fact]
    public void NonZeroArtificialDelayOutsideDevelopment_RefusesToStart()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(BaseConfig("250")));
        });

        // Program.cs throws synchronously while building the host, before app.Run() -- that
        // exception surfaces the moment the test framework forces the host to actually build,
        // which first access to the server does. (The zero-delay "starts fine" case doesn't
        // need its own test here: every other integration test in the suite already boots the
        // full app with ConcurrencyDemo:ArtificialDelayMs defaulted to 0 and proves exactly
        // that on every run.)
        Assert.ThrowsAny<Exception>(() => factory.Server);
    }
}
