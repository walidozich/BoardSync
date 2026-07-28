using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace BoardSync.Tests.Infrastructure;

// Used only by the rate-limiter test itself. Layers a low permit limit on top of
// BoardSyncApiFactory's generous default, so the limiter can be tripped in a handful of
// requests without needing its own from-scratch configuration setup.
public sealed class TightlyRateLimitedApiFactory : BoardSyncApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration(
            (_, config) =>
                config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["RateLimiting:Auth:PermitLimit"] = "3" }
                )
        );
    }
}
