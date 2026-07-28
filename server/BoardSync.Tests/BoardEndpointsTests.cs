using System.Net;
using System.Net.Http.Json;
using BoardSync.Api.Data;
using BoardSync.Api.Features.Board;
using BoardSync.Tests.Infrastructure;

namespace BoardSync.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class BoardEndpointsTests(BoardSyncApiFactory factory)
{
    [Fact]
    public async Task GetBoard_ReturnsTheSeededDemoBoard()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/board");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var board = await response.Content.ReadFromJsonAsync<BoardDto>();

        Assert.NotNull(board);
        Assert.NotEqual(Guid.Empty, board.Id);
        Assert.Equal(DbSeeder.DemoBoardName, board.Name);
    }
}
