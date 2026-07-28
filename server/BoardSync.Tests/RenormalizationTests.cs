using System.Net.Http.Json;
using BoardSync.Api.Data;
using BoardSync.Api.Data.Entities;
using BoardSync.Api.Features.Auth;
using BoardSync.Api.Features.Board;
using BoardSync.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoardSync.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class RenormalizationTests(BoardSyncApiFactory factory)
{
    private static async Task<BoardColumn> CreateEmptyColumnAsync(AppDbContext db)
    {
        var board = await db.Boards.AsNoTracking().FirstAsync();
        var column = new BoardColumn { BoardId = board.Id, Name = $"Renorm-{Guid.NewGuid():N}", Position = 9000 };
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
    public async Task MoveAsync_GapBelowThreshold_RenormalizesWholeColumnEvenlySpacedPreservingOrder()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var column = await CreateEmptyColumnAsync(db);
        var otherColumn = await CreateEmptyColumnAsync(db);
        var a = await AddCardAsync(db, column.Id, 1000, "A");
        // Gap of 0.00005 -- well under the 0.0002 (2x threshold) trigger point.
        var b = await AddCardAsync(db, column.Id, 1000.00005, "B");
        var c = await AddCardAsync(db, column.Id, 2000, "C");
        var moving = await AddCardAsync(db, otherColumn.Id, 42, "Moving card");

        try
        {
            var result = await service.MoveAsync(moving.Id, column.Id, a.Id, b.Id, moving.Version, "Test Mover");

            var success = Assert.IsType<MoveCardResult.Success>(result);
            Assert.True(success.Renormalized);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cards = await verifyDb
                .Cards.AsNoTracking()
                .Where(x => x.ColumnId == column.Id)
                .OrderBy(x => x.Position)
                .ToListAsync();

            // Original relative order (A, moving, B, C) must be preserved, now evenly spaced.
            Assert.Equal(["A", "Moving card", "B", "C"], cards.Select(x => x.Title));
            Assert.Equal([1000.0, 2000.0, 3000.0, 4000.0], cards.Select(x => x.Position));
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            cleanupDb.Cards.RemoveRange(cleanupDb.Cards.Where(x => new[] { a.Id, b.Id, c.Id, moving.Id }.Contains(x.Id)));
            cleanupDb.BoardColumns.RemoveRange(
                cleanupDb.BoardColumns.Where(x => x.Id == column.Id || x.Id == otherColumn.Id)
            );
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task MoveAsync_GapAtOrAboveThreshold_DoesNotRenormalize()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var column = await CreateEmptyColumnAsync(db);
        // Gap comfortably above the 0.0002 trigger point -- avoids asserting exact
        // floating-point boundary behavior, which is a different (and separately covered)
        // concern from "a normal-sized gap doesn't renormalize."
        var a = await AddCardAsync(db, column.Id, 1000, "A");
        var b = await AddCardAsync(db, column.Id, 1000.001, "B");
        var moving = await AddCardAsync(db, column.Id, 5000, "Moving card");

        try
        {
            var result = await service.MoveAsync(moving.Id, column.Id, a.Id, b.Id, moving.Version, "Test Mover");

            var success = Assert.IsType<MoveCardResult.Success>(result);
            Assert.False(success.Renormalized);
            Assert.Equal((1000.0 + 1000.001) / 2, success.Card.Position);

            // B's own position (and therefore version) is untouched -- only the moving card
            // was written.
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persistedB = await verifyDb.Cards.AsNoTracking().FirstAsync(x => x.Id == b.Id);
            Assert.Equal(1000.001, persistedB.Position);
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            cleanupDb.Cards.RemoveRange(cleanupDb.Cards.Where(x => new[] { a.Id, b.Id, moving.Id }.Contains(x.Id)));
            cleanupDb.BoardColumns.Remove(column);
            await cleanupDb.SaveChangesAsync();
        }
    }

    // Directly exercises the scenario spec.md describes: repeatedly dropping into the same
    // tightening slot halves the gap every time, and after roughly 23 drops the halved gap
    // would fall under the 0.0001 threshold, triggering renormalization.
    [Fact]
    public async Task MoveAsync_RepeatedlyDroppedIntoTighteningSlot_RenormalizesAroundTheTwentyThirdDrop()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var column = await CreateEmptyColumnAsync(db);
        var a = await AddCardAsync(db, column.Id, 1000, "A");
        var upper = await AddCardAsync(db, column.Id, 2000, "Upper");
        var moving = await AddCardAsync(db, column.Id, 5000, "Moving card");

        try
        {
            var drops = 0;
            var renormalizedAt = -1;
            var currentUpperId = upper.Id;

            while (renormalizedAt == -1 && drops < 30)
            {
                await using var dropScope = factory.Services.CreateAsyncScope();
                var dropDb = dropScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var dropService = dropScope.ServiceProvider.GetRequiredService<CardService>();

                var movingNow = await dropDb.Cards.AsNoTracking().FirstAsync(x => x.Id == moving.Id);
                var result = await dropService.MoveAsync(
                    moving.Id,
                    column.Id,
                    a.Id,
                    currentUpperId,
                    movingNow.Version,
                    "Test Mover"
                );

                var success = Assert.IsType<MoveCardResult.Success>(result);
                drops++;
                currentUpperId = moving.Id; // next drop tightens against the moved card itself

                if (success.Renormalized)
                {
                    renormalizedAt = drops;
                }
            }

            Assert.True(renormalizedAt is >= 20 and <= 26, $"expected renormalization around drop 23, got {renormalizedAt}");

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var aPosition = await verifyDb.Cards.AsNoTracking().Where(x => x.Id == a.Id).Select(x => x.Position).FirstAsync();
            var movingPosition = await verifyDb
                .Cards.AsNoTracking()
                .Where(x => x.Id == moving.Id)
                .Select(x => x.Position)
                .FirstAsync();

            // Post-renormalization, the gap between A and the moved card is back to a full
            // step, not a near-zero sliver.
            Assert.True(movingPosition - aPosition >= 500, "gap should have reset to a full step after renormalizing");
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            cleanupDb.Cards.RemoveRange(cleanupDb.Cards.Where(x => new[] { a.Id, upper.Id, moving.Id }.Contains(x.Id)));
            cleanupDb.BoardColumns.Remove(column);
            await cleanupDb.SaveChangesAsync();
        }
    }

    // Proves the atomicity spec.md calls for: a renormalization-eligible move whose *own*
    // version check fails must roll back the staged sibling renumbering along with the move
    // itself -- not partially apply the renumbering while rejecting only the moved card.
    [Fact]
    public async Task MoveAsync_StaleMovedCardVersionOnRenormalizationEligibleMove_RollsBackBothTheMoveAndTheRenumbering()
    {
        await using var seedScope = factory.Services.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var column = await CreateEmptyColumnAsync(seedDb);
        var a = await AddCardAsync(seedDb, column.Id, 1000, "A");
        var b = await AddCardAsync(seedDb, column.Id, 1000.00005, "B");
        var moving = await AddCardAsync(seedDb, column.Id, 5000, "Moving card");
        var staleVersion = moving.Version;

        try
        {
            // Bump the moving card's version out from under the stale request, via an
            // unrelated, real move elsewhere in the column -- ordinary write, not a hack.
            await using var bumpScope = factory.Services.CreateAsyncScope();
            var bumpService = bumpScope.ServiceProvider.GetRequiredService<CardService>();
            var bumpResult = await bumpService.MoveAsync(
                moving.Id,
                column.Id,
                afterCardId: b.Id,
                beforeCardId: null,
                staleVersion,
                "Someone Else"
            );
            Assert.IsType<MoveCardResult.Success>(bumpResult);

            await using var scope = factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<CardService>();

            // Now attempt the renormalization-eligible move (A/B's tight gap) using the
            // card's *original*, now-stale version.
            var result = await service.MoveAsync(moving.Id, column.Id, a.Id, b.Id, staleVersion, "Late Mover");

            Assert.IsType<MoveCardResult.StaleVersion>(result);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persistedA = await verifyDb.Cards.AsNoTracking().FirstAsync(x => x.Id == a.Id);
            var persistedB = await verifyDb.Cards.AsNoTracking().FirstAsync(x => x.Id == b.Id);

            // A and B were never renumbered: the whole SaveChangesAsync rolled back together.
            Assert.Equal(1000.0, persistedA.Position);
            Assert.Equal(1000.00005, persistedB.Position);
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            cleanupDb.Cards.RemoveRange(cleanupDb.Cards.Where(x => new[] { a.Id, b.Id, moving.Id }.Contains(x.Id)));
            cleanupDb.BoardColumns.Remove(column);
            await cleanupDb.SaveChangesAsync();
        }
    }

    private const string HubPath = "/hubs/board";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FairNegativeWait = TimeSpan.FromSeconds(2);

    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"user-{Guid.NewGuid():N}@example.com", "correcthorse123", "Renormalizer")
        );
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.Token;
    }

    private HubConnection BuildConnection(string accessToken)
    {
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress, $"{HubPath}?access_token={accessToken}"),
                HttpTransportType.LongPolling,
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                }
            )
            .Build();
    }

    private static async Task<HubConnection> ConnectedAndJoinedAsync(HubConnection connection)
    {
        var snapshotReceived = new TaskCompletionSource<BoardStateDto>();
        connection.On<BoardStateDto>("BoardSnapshot", s => snapshotReceived.TrySetResult(s));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinBoard");

        var completed = await Task.WhenAny(snapshotReceived.Task, Task.Delay(WaitTimeout));
        Assert.Same(snapshotReceived.Task, completed);

        return connection;
    }

    [Fact]
    public async Task MoveCard_RenormalizingMove_BroadcastsBoardSnapshotToWholeGroupInsteadOfCardMoved()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient);

        await using var seedScope = factory.Services.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var column = await CreateEmptyColumnAsync(seedDb);
        var a = await AddCardAsync(seedDb, column.Id, 1000, "A");
        var b = await AddCardAsync(seedDb, column.Id, 1000.00005, "B");
        var moving = await AddCardAsync(seedDb, column.Id, 5000, "Moving card");

        await using var mover = await ConnectedAndJoinedAsync(BuildConnection(token));
        await using var bystander = await ConnectedAndJoinedAsync(BuildConnection(token));

        // BoardSnapshot fires again on JoinBoard, so only listen for it *after* joining, to
        // isolate the one triggered by the renormalizing move itself.
        var moverSnapshot = new TaskCompletionSource<BoardStateDto>();
        mover.On<BoardStateDto>("BoardSnapshot", s => moverSnapshot.TrySetResult(s));
        var bystanderSnapshot = new TaskCompletionSource<BoardStateDto>();
        bystander.On<BoardStateDto>("BoardSnapshot", s => bystanderSnapshot.TrySetResult(s));
        var bystanderCardMoved = new TaskCompletionSource<CardMovedDto>();
        bystander.On<CardMovedDto>("CardMoved", dto => bystanderCardMoved.TrySetResult(dto));

        try
        {
            await mover.InvokeAsync(
                "MoveCard",
                new MoveCardRequest(moving.Id, column.Id, AfterCardId: a.Id, BeforeCardId: b.Id, moving.Version)
            );

            var moverCompleted = await Task.WhenAny(moverSnapshot.Task, Task.Delay(WaitTimeout));
            Assert.Same(moverSnapshot.Task, moverCompleted);

            var bystanderCompleted = await Task.WhenAny(bystanderSnapshot.Task, Task.Delay(WaitTimeout));
            Assert.Same(bystanderSnapshot.Task, bystanderCompleted);

            var snapshot = await bystanderSnapshot.Task;
            var renormalizedColumn = snapshot.Columns.Single(c => c.Id == column.Id);
            // Just three cards in this column (A, the moved card, B) -- evenly spaced.
            Assert.Equal([1000.0, 2000.0, 3000.0], renormalizedColumn.Cards.Select(c => c.Position));

            // No CardMoved should have leaked out for a renormalizing move.
            var cardMovedCompleted = await Task.WhenAny(bystanderCardMoved.Task, Task.Delay(FairNegativeWait));
            Assert.NotSame(bystanderCardMoved.Task, cardMovedCompleted);
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            cleanupDb.Cards.RemoveRange(cleanupDb.Cards.Where(x => new[] { a.Id, b.Id, moving.Id }.Contains(x.Id)));
            cleanupDb.BoardColumns.Remove(column);
            await cleanupDb.SaveChangesAsync();
        }
    }
}
