using BoardSync.Api.Data;
using BoardSync.Api.Data.Entities;
using BoardSync.Api.Features.Board;
using BoardSync.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoardSync.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class MoveCardServiceTests(BoardSyncApiFactory factory)
{
    private static async Task<Guid> GetSeededColumnIdAsync(AppDbContext db, string columnName)
    {
        var column = await db.BoardColumns.AsNoTracking().FirstAsync(c => c.Name == columnName);
        return column.Id;
    }

    private static async Task<BoardColumn> CreateEmptyColumnAsync(AppDbContext db)
    {
        var board = await db.Boards.AsNoTracking().FirstAsync();
        var column = new BoardColumn { BoardId = board.Id, Name = $"Empty-{Guid.NewGuid():N}", Position = 9000 };
        db.BoardColumns.Add(column);
        await db.SaveChangesAsync();
        return column;
    }

    private static async Task<Card> AddCardAsync(AppDbContext db, Guid columnId, double position, string title)
    {
        var card = new Card
        {
            ColumnId = columnId,
            Title = title,
            Position = position,
        };
        db.Cards.Add(card);
        await db.SaveChangesAsync();
        return card;
    }

    [Fact]
    public async Task MoveAsync_BothNeighboursPresentInTargetColumn_LandsAtMidpoint()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var column = await CreateEmptyColumnAsync(db);
        var moving = await AddCardAsync(db, column.Id, 5000, "Moving card");
        var after = await AddCardAsync(db, column.Id, 1000, "After card");
        var before = await AddCardAsync(db, column.Id, 2000, "Before card");

        try
        {
            var result = await service.MoveAsync(moving.Id, column.Id, after.Id, before.Id, 0);

            var success = Assert.IsType<MoveCardResult.Success>(result);
            Assert.Equal((1000.0 + 2000.0) / 2, success.Card.Position);
            Assert.Equal(column.Id, success.Card.ColumnId);
        }
        finally
        {
            db.Cards.RemoveRange(moving, after, before);
            db.BoardColumns.Remove(column);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task MoveAsync_NoAfterCardValidBeforeCard_LandsAtHalfOfBeforePosition()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var column = await CreateEmptyColumnAsync(db);
        var moving = await AddCardAsync(db, column.Id, 5000, "Moving card");
        var before = await AddCardAsync(db, column.Id, 2000, "Before card");

        try
        {
            var result = await service.MoveAsync(moving.Id, column.Id, afterCardId: null, before.Id, 0);

            var success = Assert.IsType<MoveCardResult.Success>(result);
            Assert.Equal(2000.0 / 2, success.Card.Position);
        }
        finally
        {
            db.Cards.RemoveRange(moving, before);
            db.BoardColumns.Remove(column);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task MoveAsync_ValidAfterCardNoBeforeCard_LandsAtAfterPositionPlus1000()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var column = await CreateEmptyColumnAsync(db);
        var moving = await AddCardAsync(db, column.Id, 5000, "Moving card");
        var after = await AddCardAsync(db, column.Id, 1000, "After card");

        try
        {
            var result = await service.MoveAsync(moving.Id, column.Id, after.Id, beforeCardId: null, 0);

            var success = Assert.IsType<MoveCardResult.Success>(result);
            Assert.Equal(1000.0 + 1000.0, success.Card.Position);
        }
        finally
        {
            db.Cards.RemoveRange(moving, after);
            db.BoardColumns.Remove(column);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task MoveAsync_BothNeighboursNullTargetColumnOtherwiseEmpty_LandsAt1000()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sourceColumn = await CreateEmptyColumnAsync(db);
        var targetColumn = await CreateEmptyColumnAsync(db);
        var moving = await AddCardAsync(db, sourceColumn.Id, 5000, "Moving card");

        try
        {
            var result = await service.MoveAsync(moving.Id, targetColumn.Id, afterCardId: null, beforeCardId: null, 0);

            var success = Assert.IsType<MoveCardResult.Success>(result);
            Assert.Equal(1000, success.Card.Position);
            Assert.Equal(targetColumn.Id, success.Card.ColumnId);
        }
        finally
        {
            db.Cards.Remove(moving);
            db.BoardColumns.RemoveRange(sourceColumn, targetColumn);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task MoveAsync_NamedNeighbourIdDoesNotExistAtAll_FallsBackToBottomOfColumn()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var column = await CreateEmptyColumnAsync(db);
        var moving = await AddCardAsync(db, column.Id, 500, "Moving card");
        var existing = await AddCardAsync(db, column.Id, 3000, "Existing card");
        var ghostId = Guid.NewGuid();

        try
        {
            var result = await service.MoveAsync(moving.Id, column.Id, ghostId, beforeCardId: null, 0);

            var success = Assert.IsType<MoveCardResult.Success>(result);
            // Bottom of column excluding the moved card itself: max(existing.Position) + 1000.
            Assert.Equal(3000.0 + 1000.0, success.Card.Position);
        }
        finally
        {
            db.Cards.RemoveRange(moving, existing);
            db.BoardColumns.Remove(column);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task MoveAsync_NamedNeighbourExistsButInDifferentColumn_FallsBackToBottomOfColumn()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var targetColumn = await CreateEmptyColumnAsync(db);
        var otherColumn = await CreateEmptyColumnAsync(db);
        var moving = await AddCardAsync(db, targetColumn.Id, 500, "Moving card");
        var existingInTarget = await AddCardAsync(db, targetColumn.Id, 3000, "Existing target card");
        // Neighbour named by the client but was moved to a different column since the client
        // last saw the board -- exists, but isn't in the target column anymore.
        var elsewhere = await AddCardAsync(db, otherColumn.Id, 100, "Elsewhere card");

        try
        {
            var result = await service.MoveAsync(moving.Id, targetColumn.Id, elsewhere.Id, beforeCardId: null, 0);

            var success = Assert.IsType<MoveCardResult.Success>(result);
            Assert.Equal(3000.0 + 1000.0, success.Card.Position);
        }
        finally
        {
            db.Cards.RemoveRange(moving, existingInTarget, elsewhere);
            db.BoardColumns.RemoveRange(targetColumn, otherColumn);
            await db.SaveChangesAsync();
        }
    }

    // This is the specific bug the "exclude the card being moved" instruction exists to prevent:
    // the card being reordered is itself the current bottom of the column (highest position), so
    // if the bottom-of-column fallback calculation failed to exclude it, the "current max" would
    // be the moving card's own prior position, and its recomputed position would come out
    // strictly above where it already was, when it should just settle underneath the *other*
    // cards in the column instead of runaway-incrementing off its own old spot.
    [Fact]
    public async Task MoveAsync_ReorderWithinOwnColumnAndStaleNeighbour_ExcludesMovedCardsOwnPriorPositionFromFallback()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var column = await CreateEmptyColumnAsync(db);
        var other = await AddCardAsync(db, column.Id, 1000, "Other card");
        // The moving card already holds the highest position in the column.
        var moving = await AddCardAsync(db, column.Id, 9000, "Moving card");
        var ghostId = Guid.NewGuid();

        try
        {
            var result = await service.MoveAsync(moving.Id, column.Id, ghostId, beforeCardId: null, 0);

            var success = Assert.IsType<MoveCardResult.Success>(result);
            // Correct (moving card excluded from the max calc): max is `other` at 1000, so the
            // fallback lands at 1000 + 1000 = 2000.
            // If the exclusion were missing, the max would include the moving card's own prior
            // position of 9000, producing 9000 + 1000 = 10000 instead -- this assertion would
            // fail under that bug.
            Assert.Equal(1000.0 + 1000.0, success.Card.Position);
            Assert.NotEqual(9000.0 + 1000.0, success.Card.Position);
        }
        finally
        {
            db.Cards.RemoveRange(moving, other);
            db.BoardColumns.Remove(column);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task MoveAsync_UnknownCardId_ReturnsCardNotFound()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var columnId = await GetSeededColumnIdAsync(db, "Backlog");

        var result = await service.MoveAsync(Guid.NewGuid(), columnId, null, null, 0);

        Assert.IsType<MoveCardResult.CardNotFound>(result);
    }

    [Fact]
    public async Task MoveAsync_UnknownTargetColumnId_ReturnsColumnNotFound()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var columnId = await GetSeededColumnIdAsync(db, "Backlog");

        var card = await AddCardAsync(db, columnId, 42, "A card");

        try
        {
            var result = await service.MoveAsync(card.Id, Guid.NewGuid(), null, null, 0);

            Assert.IsType<MoveCardResult.ColumnNotFound>(result);
        }
        finally
        {
            db.Cards.Remove(card);
            await db.SaveChangesAsync();
        }
    }
}
