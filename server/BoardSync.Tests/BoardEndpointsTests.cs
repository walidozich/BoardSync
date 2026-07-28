using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BoardSync.Api.Data;
using BoardSync.Api.Features.Auth;
using BoardSync.Api.Features.Board;
using BoardSync.Tests.Infrastructure;

namespace BoardSync.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class BoardEndpointsTests(BoardSyncApiFactory factory)
{
    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"user-{Guid.NewGuid():N}@example.com", "correcthorse123", "Alice")
        );
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.Token;
    }

    [Fact]
    public async Task GetBoard_ReturnsTheSeededDemoBoard()
    {
        var client = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/board");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var board = await response.Content.ReadFromJsonAsync<BoardStateDto>();

        Assert.NotNull(board);
        Assert.NotEqual(Guid.Empty, board.Id);
        Assert.Equal(DbSeeder.DemoBoardName, board.Name);
        Assert.NotEmpty(board.Columns);
    }

    [Fact]
    public async Task GetBoard_WithNoToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/board");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
