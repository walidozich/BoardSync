using BoardSync.Api.Data;
using BoardSync.Api.Data.Entities;
using BoardSync.Api.Features.Board;
using BoardSync.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoardSync.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class CardServiceTests(BoardSyncApiFactory factory)
{
    private static async Task<Guid> GetSeededColumnIdAsync(AppDbContext db, string columnName)
    {
        var column = await db.BoardColumns.AsNoTracking().FirstAsync(c => c.Name == columnName);
        return column.Id;
    }

    [Fact]
    public async Task CreateAsync_InColumnWithExistingCards_LandsAtBottom()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var columnId = await GetSeededColumnIdAsync(db, "Backlog");
        var maxExistingPosition = await db
            .Cards.Where(c => c.ColumnId == columnId)
            .MaxAsync(c => (double?)c.Position);

        Card? created = null;
        try
        {
            var result = await service.CreateAsync(columnId, "A new bottom card", "some description");

            var success = Assert.IsType<CreateCardResult.Success>(result);
            created = success.Card;

            Assert.Equal(maxExistingPosition!.Value + 1000, success.Card.Position);
            Assert.Equal("A new bottom card", success.Card.Title);
            Assert.Equal("some description", success.Card.Description);
            Assert.NotEqual(0u, success.Card.Version);
        }
        finally
        {
            if (created is not null)
            {
                db.Cards.Remove(created);
                await db.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task CreateAsync_InEmptyColumn_LandsAtPosition1000()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var board = await db.Boards.AsNoTracking().FirstAsync();
        var emptyColumn = new BoardColumn { BoardId = board.Id, Name = $"Empty-{Guid.NewGuid():N}", Position = 9000 };
        db.BoardColumns.Add(emptyColumn);
        await db.SaveChangesAsync();

        Card? created = null;
        try
        {
            var result = await service.CreateAsync(emptyColumn.Id, "First card in empty column", null);

            var success = Assert.IsType<CreateCardResult.Success>(result);
            created = success.Card;

            Assert.Equal(1000, success.Card.Position);
            Assert.Null(success.Card.Description);
        }
        finally
        {
            if (created is not null)
            {
                db.Cards.Remove(created);
            }
            db.BoardColumns.Remove(emptyColumn);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task CreateAsync_EmptyTitle_ReturnsValidationFailedWithTitleError()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var columnId = await GetSeededColumnIdAsync(db, "Backlog");

        var result = await service.CreateAsync(columnId, "   ", null);

        var failed = Assert.IsType<CreateCardResult.ValidationFailed>(result);
        Assert.True(failed.Errors.ContainsKey(nameof(CreateCardRequest.Title)));
    }

    [Fact]
    public async Task CreateAsync_TitleOver200Characters_ReturnsValidationFailedWithTitleError()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var columnId = await GetSeededColumnIdAsync(db, "Backlog");

        var result = await service.CreateAsync(columnId, new string('a', 201), null);

        var failed = Assert.IsType<CreateCardResult.ValidationFailed>(result);
        Assert.True(failed.Errors.ContainsKey(nameof(CreateCardRequest.Title)));
    }

    [Fact]
    public async Task CreateAsync_DescriptionOver2000Characters_ReturnsValidationFailedWithDescriptionError()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var columnId = await GetSeededColumnIdAsync(db, "Backlog");

        var result = await service.CreateAsync(columnId, "Valid title", new string('b', 2001));

        var failed = Assert.IsType<CreateCardResult.ValidationFailed>(result);
        Assert.True(failed.Errors.ContainsKey(nameof(CreateCardRequest.Description)));
    }

    [Fact]
    public async Task CreateAsync_UnknownColumnId_ReturnsColumnNotFound()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();

        var result = await service.CreateAsync(Guid.NewGuid(), "A card", null);

        Assert.IsType<CreateCardResult.ColumnNotFound>(result);
    }

    [Fact]
    public async Task CreateAsync_WhenBoardIsAtTheCap_ReturnsBoardFullAndLeavesOtherTestsUnaffected()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var columnId = await GetSeededColumnIdAsync(db, "Backlog");

        const int boardCap = 200;
        var currentCount = await db.Cards.CountAsync();
        var fillerCount = boardCap - currentCount;
        Assert.True(fillerCount > 0, "Seed data already at or above the cap -- test assumption broken.");

        var fillerCards = Enumerable
            .Range(0, fillerCount)
            .Select(i => new Card
            {
                ColumnId = columnId,
                Title = $"Filler {i}",
                Position = 100_000 + i,
            })
            .ToList();

        db.Cards.AddRange(fillerCards);
        await db.SaveChangesAsync();

        try
        {
            var totalAfterFill = await db.Cards.CountAsync();
            Assert.Equal(boardCap, totalAfterFill);

            var result = await service.CreateAsync(columnId, "One too many", null);

            Assert.IsType<CreateCardResult.BoardFull>(result);
        }
        finally
        {
            // Remove exactly the filler rows this test inserted, so the seeded card count (and
            // per-column counts BoardStateTests asserts on) are restored for every other test in
            // this shared-fixture collection.
            var fillerIds = fillerCards.Select(c => c.Id).ToList();
            var toRemove = await db.Cards.Where(c => fillerIds.Contains(c.Id)).ToListAsync();
            db.Cards.RemoveRange(toRemove);
            await db.SaveChangesAsync();

            var totalAfterCleanup = await db.Cards.CountAsync();
            Assert.Equal(currentCount, totalAfterCleanup);
        }
    }
}
