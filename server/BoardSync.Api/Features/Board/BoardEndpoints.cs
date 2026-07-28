using BoardSync.Api.Data;

namespace BoardSync.Api.Features.Board;

public record BoardStateDto(Guid Id, string Name, IReadOnlyList<BoardColumnDto> Columns);

public record BoardColumnDto(Guid Id, string Name, double Position, IReadOnlyList<CardDto> Cards);

public record CardDto(Guid Id, string Title, string? Description, double Position, uint Version);

public static class BoardEndpoints
{
    public static void MapBoardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/board", async (AppDbContext db) =>
            {
                var board = await BoardQueries.GetBoardStateAsync(db);

                return board is null ? Results.NotFound() : Results.Ok(board);
            })
            .RequireAuthorization();
    }
}
