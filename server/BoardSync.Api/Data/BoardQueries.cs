using BoardSync.Api.Features.Board;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Data;

/// <summary>
/// Loads the full nested board state (board -> columns -> cards). Shared between the
/// GET /api/board REST handler and BoardHub.JoinBoard so both surfaces stay in sync
/// without duplicating the query.
/// </summary>
public static class BoardQueries
{
    public static async Task<BoardStateDto?> GetBoardStateAsync(AppDbContext db)
    {
        var board = await db.Boards.AsNoTracking().FirstOrDefaultAsync();

        if (board is null)
        {
            return null;
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

        return new BoardStateDto(board.Id, board.Name, columnDtos);
    }

    /// <summary>
    /// Lightweight lookup for callers that only need the board's id (e.g. CardService's
    /// broadcast group name) and would otherwise waste a full board+columns+cards load just to
    /// read one field. Null when no board is seeded, same as GetBoardStateAsync.
    /// </summary>
    public static Task<Guid?> GetBoardIdAsync(AppDbContext db) =>
        db.Boards.AsNoTracking().Select(b => (Guid?)b.Id).FirstOrDefaultAsync();
}
