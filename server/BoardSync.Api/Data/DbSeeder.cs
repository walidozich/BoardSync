using BoardSync.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Data;

public static class DbSeeder
{
    public const string DemoBoardName = "BoardSync Demo";

    public static async Task SeedAsync(AppDbContext db)
    {
        // Checked per-stage (board, then columns) rather than one global "anything seeded
        // yet?" gate. A single gate on Boards.AnyAsync() would silently stop this method from
        // ever seeding columns/cards on a database that already has the board from an earlier
        // phase's run — exactly what happened to the persistent dev database once this phase
        // added column/card seeding on top of phase 1's board-only seed.
        var board = await db.Boards.FirstOrDefaultAsync(b => b.Name == DemoBoardName);
        if (board is null)
        {
            board = new Board { Name = DemoBoardName };
            db.Boards.Add(board);
            await db.SaveChangesAsync();
        }

        if (await db.BoardColumns.AnyAsync(c => c.BoardId == board.Id))
        {
            return;
        }

        var backlog = new BoardColumn { BoardId = board.Id, Name = "Backlog", Position = 1000 };
        var inProgress = new BoardColumn { BoardId = board.Id, Name = "In Progress", Position = 2000 };
        var review = new BoardColumn { BoardId = board.Id, Name = "Review", Position = 3000 };
        var done = new BoardColumn { BoardId = board.Id, Name = "Done", Position = 4000 };
        db.BoardColumns.AddRange(backlog, inProgress, review, done);

        db.Cards.AddRange(
            new Card
            {
                ColumnId = backlog.Id,
                Title = "Set up the SignalR hub",
                Description = "Wire up the real-time hub for board updates.",
                Position = 1000,
            },
            new Card
            {
                ColumnId = backlog.Id,
                Title = "Design the card positioning scheme",
                Description = "Fractional positions so reordering doesn't require a rewrite of every row.",
                Position = 2000,
            },
            new Card
            {
                ColumnId = inProgress.Id,
                Title = "Build the columns and cards data model",
                Position = 1000,
            },
            new Card
            {
                ColumnId = inProgress.Id,
                Title = "Write the concurrency integration test",
                Description = "Cover the xmin-based optimistic concurrency check.",
                Position = 2000,
            },
            new Card
            {
                ColumnId = review.Id,
                Title = "Build the presence roster",
                Position = 1000,
            },
            new Card
            {
                ColumnId = review.Id,
                Title = "Review the JWT auth flow",
                Description = null,
                Position = 2000,
            },
            new Card
            {
                ColumnId = done.Id,
                Title = "Seed the demo board",
                Position = 1000,
            },
            new Card
            {
                ColumnId = done.Id,
                Title = "Deploy to production",
                Description = "Ship the first cut of the API.",
                Position = 2000,
            }
        );

        await db.SaveChangesAsync();
    }
}
