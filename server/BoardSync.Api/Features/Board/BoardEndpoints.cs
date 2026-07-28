using BoardSync.Api.Data;
using Microsoft.EntityFrameworkCore;

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
            var board = await db.Boards.AsNoTracking().FirstOrDefaultAsync();

            if (board is null)
            {
                return Results.NotFound();
            }

            var columns = await db
                .BoardColumns.AsNoTracking()
                .Where(c => c.BoardId == board.Id)
                .OrderBy(c => c.Position)
                .ToListAsync();

            var columnIds = columns.Select(c => c.Id).ToList();

            var cards = await db
                .Cards.AsNoTracking()
                .Where(c => columnIds.Contains(c.ColumnId))
                .OrderBy(c => c.Position)
                .ToListAsync();

            var cardsByColumn = cards.GroupBy(c => c.ColumnId).ToDictionary(g => g.Key, g => g.ToList());

            var columnDtos = columns
                .Select(c => new BoardColumnDto(
                    c.Id,
                    c.Name,
                    c.Position,
                    (cardsByColumn.TryGetValue(c.Id, out var columnCards) ? columnCards : [])
                        .Select(card => new CardDto(
                            card.Id,
                            card.Title,
                            card.Description,
                            card.Position,
                            card.Version
                        ))
                        .ToList()
                ))
                .ToList();

            return Results.Ok(new BoardStateDto(board.Id, board.Name, columnDtos));
        });
    }
}
