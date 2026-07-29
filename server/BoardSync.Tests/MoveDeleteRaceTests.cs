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

// The "three outcomes, not two" phase: a DbUpdateConcurrencyException during MoveAsync is
// ambiguous between "someone else moved it first" (StaleVersion) and "someone else deleted it
// mid-drag" (CardDeleted). Conflating them is how the loser of a delete race watches a card
// resurrect itself -- these tests force the delete-during-move race deterministically, the
// same way phase 9's ConcurrencyTests.cs and phase 11's DeleteCardTests.cs do, rather than
// hoping two operations happen to interleave.
[Collection(DatabaseCollection.Name)]
public sealed class MoveDeleteRaceTests(BoardSyncApiFactory factory)
{
    private static async Task<BoardColumn> CreateEmptyColumnAsync(AppDbContext db)
    {
        var board = await db.Boards.AsNoTracking().FirstAsync();
        var column = new BoardColumn { BoardId = board.Id, Name = $"Race-{Guid.NewGuid():N}", Position = 9000 };
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
    public async Task MoveAsync_CardDeletedDuringTheMove_ReturnsCardDeletedNotStaleVersion()
    {
        await using var seedScope = factory.Services.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var column = await CreateEmptyColumnAsync(seedDb);
        var otherColumn = await CreateEmptyColumnAsync(seedDb);
        var card = await AddCardAsync(seedDb, column.Id, 1000, "Card to race");
        var originalVersion = card.Version;

        // Same row-lock coordination as DeleteCardTests.cs's idempotent-race test: a plain
        // sequential "delete, then move" would just make MoveAsync's own initial SELECT find
        // nothing and return CardNotFound immediately -- a completely different code path from
        // the one this test exists to prove (the DbUpdateConcurrencyException catch block's
        // null-re-query branch). The row lock forces the mover's UPDATE to actually be
        // in-flight when the delete commits underneath it.
        await using var deleterScope = factory.Services.CreateAsyncScope();
        var deleterDb = deleterScope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var moverScope = factory.Services.CreateAsyncScope();
        var moverService = moverScope.ServiceProvider.GetRequiredService<CardService>();

        try
        {
            await using var deleteTransaction = await deleterDb.Database.BeginTransactionAsync();
            var cardToDelete = await deleterDb.Cards.FirstAsync(c => c.Id == card.Id);
            deleterDb.Cards.Remove(cardToDelete);
            deleterDb.Entry(cardToDelete).Property(c => c.Version).OriginalValue = originalVersion;
            await deleterDb.SaveChangesAsync(); // DELETE executed, not yet committed -- row lock held

            var moveTask = moverService.MoveAsync(
                card.Id,
                otherColumn.Id,
                afterCardId: null,
                beforeCardId: null,
                originalVersion,
                "Mover"
            );

            // Generous margin for the move's UPDATE to reach the lock wait before the delete
            // commits and unblocks it -- not for correctness of the assertion (that's the row
            // lock), only to keep the interleaving deterministic.
            await Task.Delay(200);
            await deleteTransaction.CommitAsync();

            var result = await moveTask;

            Assert.IsType<MoveCardResult.CardDeleted>(result);

            // Confirm the row is genuinely gone, not merely that the result type says so.
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await verifyDb.Cards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == card.Id);
            Assert.Null(persisted);
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            cleanupDb.Cards.RemoveRange(cleanupDb.Cards.Where(c => c.Id == card.Id));
            cleanupDb.BoardColumns.RemoveRange(
                cleanupDb.BoardColumns.Where(c => c.Id == column.Id || c.Id == otherColumn.Id)
            );
            await cleanupDb.SaveChangesAsync();
        }
    }

    private const string HubPath = "/hubs/board";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client, string displayName)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"user-{Guid.NewGuid():N}@example.com", "correcthorse123", displayName)
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

    private async Task<Guid> GetSeededColumnIdAsync(string columnName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var column = await db.BoardColumns.AsNoTracking().FirstAsync(c => c.Name == columnName);
        return column.Id;
    }

    private async Task<Card> AddCardAsync(Guid columnId, double position, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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

    // Hub-level: proves MoveRejected(CardDeleted) actually reaches the mover through the real
    // SignalR pipeline, caller-only, with the minimal payload spec.md specifies (no Card, no
    // WinnerDisplayName -- unlike StaleVersion, there's no "authoritative position" to snap to
    // and no useful winner name once the row is gone). The row-lock is held directly against
    // the database (not through DeleteCard) to keep the forcing mechanism deterministic; the
    // CardService-level test above already proves DeleteAsync itself produces the same
    // underlying row-vanishes effect that this lock simulates.
    [Fact]
    public async Task MoveCard_CardDeletedDuringTheMove_RejectsCallerOnlyWithMinimalPayload()
    {
        var httpClient = factory.CreateClient();
        var moverToken = await RegisterAndGetTokenAsync(httpClient, "Mover");
        var columnId = await GetSeededColumnIdAsync("Backlog");
        var otherColumnId = await GetSeededColumnIdAsync("In Progress");
        var card = await AddCardAsync(columnId, 500, "Card to race");
        var originalVersion = card.Version;

        await using var mover = await ConnectedAndJoinedAsync(BuildConnection(moverToken));

        var rejected = new TaskCompletionSource<MoveRejectedDto>();
        mover.On<MoveRejectedDto>("MoveRejected", dto => rejected.TrySetResult(dto));
        var moverReceivedMoved = new TaskCompletionSource<CardMovedDto>();
        mover.On<CardMovedDto>("CardMoved", dto => moverReceivedMoved.TrySetResult(dto));

        await using var deleterScope = factory.Services.CreateAsyncScope();
        var deleterDb = deleterScope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            await using var deleteTransaction = await deleterDb.Database.BeginTransactionAsync();
            var cardToDelete = await deleterDb.Cards.FirstAsync(c => c.Id == card.Id);
            deleterDb.Cards.Remove(cardToDelete);
            deleterDb.Entry(cardToDelete).Property(c => c.Version).OriginalValue = originalVersion;
            await deleterDb.SaveChangesAsync();

            var moveInvokeTask = mover.InvokeAsync(
                "MoveCard",
                new MoveCardRequest(card.Id, otherColumnId, AfterCardId: null, BeforeCardId: null, originalVersion)
            );

            await Task.Delay(200);
            await deleteTransaction.CommitAsync();
            await moveInvokeTask;

            var rejectedCompleted = await Task.WhenAny(rejected.Task, Task.Delay(WaitTimeout));
            Assert.Same(rejected.Task, rejectedCompleted);
            var rejectedDto = await rejected.Task;

            Assert.Equal(nameof(RejectReason.CardDeleted), rejectedDto.Reason);
            Assert.Equal(card.Id, rejectedDto.CardId);
            Assert.Null(rejectedDto.Card);
            Assert.Null(rejectedDto.WinnerDisplayName);

            // No CardMoved leaked out for a move that was actually rejected.
            Assert.False(moverReceivedMoved.Task.IsCompleted);
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var remaining = await cleanupDb.Cards.FindAsync(card.Id);
            if (remaining is not null)
            {
                cleanupDb.Cards.Remove(remaining);
                await cleanupDb.SaveChangesAsync();
            }
        }
    }
}
