using System.Net;
using System.Net.Http.Json;
using BoardSync.Api.Features.Auth;
using BoardSync.Tests.Infrastructure;

namespace BoardSync.Tests;

// Deliberately not in the shared "Database" collection: this test exhausts the per-IP rate
// limit budget by design, and every other auth test shares that same budget when they share
// a factory. An isolated IClassFixture gives this test its own app instance (and its own
// throwaway Postgres container) so it can't 429 its siblings, and a tight per-test permit
// limit means it doesn't need dozens of requests to prove the limiter trips.
public sealed class AuthRateLimiterTests(TightlyRateLimitedApiFactory factory)
    : IClassFixture<TightlyRateLimitedApiFactory>
{
    [Fact]
    public async Task Login_RepeatedRequests_EventuallyRateLimited()
    {
        var client = factory.CreateClient();
        const string email = "rate-limit-probe@example.com";

        var statusCodes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest(email, "whatever-password")
            );
            statusCodes.Add(response.StatusCode);
        }

        Assert.Contains((HttpStatusCode)429, statusCodes);
    }
}
