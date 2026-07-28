using BoardSync.Api.Data;

namespace BoardSync.Api.Features.Board;

public record BoardStateDto(Guid Id, string Name, IReadOnlyList<BoardColumnDto> Columns);

public record BoardColumnDto(Guid Id, string Name, double Position, IReadOnlyList<CardDto> Cards);

public record CardDto(Guid Id, string Title, string? Description, double Position, uint Version);

public record CreateCardRequest(Guid ColumnId, string Title, string? Description);

public record CardCreatedDto(Guid Id, Guid ColumnId, string Title, string? Description, double Position, uint Version);

public record CreateCardRejectedDto(string Reason, IReadOnlyDictionary<string, string[]>? Errors);

public record MoveCardRequest(Guid CardId, Guid TargetColumnId, Guid? AfterCardId, Guid? BeforeCardId, uint ExpectedVersion);

public record CardMovedDto(Guid Id, Guid ColumnId, double Position, uint Version);

public record MoveRejectedDto(string Reason, Guid CardId, CardMovedDto? Card, string? WinnerDisplayName);

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
