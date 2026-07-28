using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoardSync.Api.Features.Auth;
using BoardSync.Tests.Infrastructure;

namespace BoardSync.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class AuthEndpointsTests(BoardSyncApiFactory factory)
{
    private static string UniqueEmail() => $"user-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Register_WithValidData_ReturnsTokenAndDisplayName()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, "correcthorse123", "Alice")
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        Assert.Equal("Alice", body.DisplayName);
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ReturnsConflict_RegardlessOfCase()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        var first = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, "correcthorse123", "Alice")
        );
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email.ToUpperInvariant(), "correcthorse123", "Alice2")
        );

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Register_ResponseBody_NeverContainsPasswordOrHash()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, "correcthorse123", "Alice")
        );

        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            Assert.DoesNotContain("password", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hash", property.Name, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("correcthorse123", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_ReturnsToken()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();
        const string password = "correcthorse123";

        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, password, "Alice"));

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, "correcthorse123", "Alice")
        );

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(email, "totally-wrong-password")
        );

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownEmailAndWrongPassword_ReturnIdenticalResponses()
    {
        // Guards against user-enumeration: an attacker must not be able to tell "no such
        // account" apart from "wrong password" by status code or response body.
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, "correcthorse123", "Alice")
        );

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(email, "totally-wrong-password")
        );
        var unknownEmail = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(UniqueEmail(), "whatever-password")
        );

        Assert.Equal(wrongPassword.StatusCode, unknownEmail.StatusCode);
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(),
            await unknownEmail.Content.ReadAsStringAsync()
        );
    }

}
