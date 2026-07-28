using BoardSync.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Features.Board;

public record BoardDto(Guid Id, string Name);

public static class BoardEndpoints
{
    public static void MapBoardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/board", async (AppDbContext db) =>
        {
            var board = await db.Boards.AsNoTracking().FirstOrDefaultAsync();

            return board is null
                ? Results.NotFound()
                : Results.Ok(new BoardDto(board.Id, board.Name));
        });
    }
}
