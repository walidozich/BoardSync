using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BoardSync.Api.Features.Auth;
using BoardSync.Api.Features.Board;
using BoardSync.Tests.Infrastructure;

namespace BoardSync.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class BoardStateTests(BoardSyncApiFactory factory)
{
    private static async Task<HttpClient> AuthenticatedClientAsync(BoardSyncApiFactory factory)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"user-{Guid.NewGuid():N}@example.com", "correcthorse123", "Alice")
        );
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }

    [Fact]
    public async Task GetBoard_ReturnsColumnsInSeededOrderAndPosition()
    {
        var client = await AuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/board");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var board = await response.Content.ReadFromJsonAsync<BoardStateDto>();
        Assert.NotNull(board);

        Assert.Equal(4, board.Columns.Count);
        Assert.Equal(["Backlog", "In Progress", "Review", "Done"], board.Columns.Select(c => c.Name));
        Assert.Equal([1000, 2000, 3000, 4000], board.Columns.Select(c => c.Position));
    }

    [Fact]
    public async Task GetBoard_ReturnsCardsAssignedToCorrectColumnsWithExpectedCount()
    {
        var client = await AuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/board");
        var board = await response.Content.ReadFromJsonAsync<BoardStateDto>();
        Assert.NotNull(board);

        // Cards within each column come back ordered by position ascending.
        foreach (var column in board.Columns)
        {
            var positions = column.Cards.Select(c => c.Position).ToList();
            Assert.Equal(positions.OrderBy(p => p).ToList(), positions);
        }

        Assert.Equal(2, board.Columns[0].Cards.Count); // Backlog
        Assert.Equal(2, board.Columns[1].Cards.Count); // In Progress
        Assert.Equal(2, board.Columns[2].Cards.Count); // Review
        Assert.Equal(2, board.Columns[3].Cards.Count); // Done

        var totalCards = board.Columns.Sum(c => c.Cards.Count);
        Assert.Equal(8, totalCards);
    }

    [Fact]
    public async Task GetBoard_EveryCardHasNonZeroVersion()
    {
        var client = await AuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/board");
        var board = await response.Content.ReadFromJsonAsync<BoardStateDto>();
        Assert.NotNull(board);

        var allCards = board.Columns.SelectMany(c => c.Cards).ToList();
        Assert.NotEmpty(allCards);
        Assert.All(allCards, card => Assert.NotEqual(0u, card.Version));
    }
}
