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
public sealed class DeleteCardTests(BoardSyncApiFactory factory)
{
    private static async Task<BoardColumn> CreateEmptyColumnAsync(AppDbContext db)
    {
        var board = await db.Boards.AsNoTracking().FirstAsync();
        var column = new BoardColumn { BoardId = board.Id, Name = $"Delete-{Guid.NewGuid():N}", Position = 9000 };
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
    public async Task DeleteAsync_CurrentVersion_SucceedsAndRemovesTheRowFromTheDatabase()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var column = await CreateEmptyColumnAsync(db);
        var card = await AddCardAsync(db, column.Id, 1000, "Card to delete");

        try
        {
            var result = await service.DeleteAsync(card.Id, card.Version);

            Assert.IsType<DeleteCardResult.Success>(result);

            // Real evidence, not just the in-memory result: a fresh, untracked read proves
            // the row is actually gone from the database.
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
            cleanupDb.BoardColumns.RemoveRange(cleanupDb.BoardColumns.Where(c => c.Id == column.Id));
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task DeleteAsync_UnknownCardId_ReturnsCardNotFound()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CardService>();

        var result = await service.DeleteAsync(Guid.NewGuid(), expectedVersion: 0);

        Assert.IsType<DeleteCardResult.CardNotFound>(result);
    }

    [Fact]
    public async Task DeleteAsync_StaleVersion_IsRejectedWithAuthoritativeStateAndWinnerName()
    {
        await using var seedScope = factory.Services.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var column = await CreateEmptyColumnAsync(seedDb);
        var otherColumn = await CreateEmptyColumnAsync(seedDb);
        var card = await AddCardAsync(seedDb, column.Id, 1000, "Card to delete");
        var originalVersion = card.Version;

        try
        {
            // Bump the card's version out from under the stale delete request via a real,
            // separate successful move -- not a hack, an ordinary write.
            await using var bumpScope = factory.Services.CreateAsyncScope();
            var bumpService = bumpScope.ServiceProvider.GetRequiredService<CardService>();
            var bumpResult = await bumpService.MoveAsync(
                card.Id,
                otherColumn.Id,
                afterCardId: null,
                beforeCardId: null,
                originalVersion,
                "Ahmed"
            );
            var bumpSuccess = Assert.IsType<MoveCardResult.Success>(bumpResult);

            await using var scope = factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<CardService>();

            var result = await service.DeleteAsync(card.Id, originalVersion);

            var stale = Assert.IsType<DeleteCardResult.StaleVersion>(result);
            Assert.Equal("Ahmed", stale.WinnerDisplayName);
            Assert.Equal(otherColumn.Id, stale.AuthoritativeCard.ColumnId);
            Assert.Equal(bumpSuccess.Card.Version, stale.AuthoritativeCard.Version);

            // The row is untouched: the rejected delete never removed it.
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await verifyDb.Cards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == card.Id);
            Assert.NotNull(persisted);
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

    [Fact]
    public async Task DeleteAsync_TwoDbContextsRaceToDeleteTheSameCard_SecondIsIdempotentSuccessNotStale()
    {
        await using var seedScope = factory.Services.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var column = await CreateEmptyColumnAsync(seedDb);
        var card = await AddCardAsync(seedDb, column.Id, 1000, "Racing delete card");
        var originalVersion = card.Version;

        // Two independent DI scopes, each with its own AppDbContext, exactly like two separate
        // hub invocations from two separate users racing to delete the same card.
        await using var scopeA = factory.Services.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var scopeB = factory.Services.CreateAsyncScope();
        var serviceB = scopeB.ServiceProvider.GetRequiredService<CardService>();

        try
        {
            // Unlike MoveAsync's race (ConcurrencyTests), this can't be two sequential awaits,
            // nor even two Task.Run calls without coordination: a delete makes the row vanish
            // entirely, so a second DeleteAsync call started after the first one has already
            // committed finds nothing on its own initial SELECT and returns CardNotFound,
            // never reaching the concurrency-conflict path this test exists to prove. Against
            // a local Testcontainers Postgres both operations complete fast enough that even
            // parallel Task.Run calls reliably finish one before the other starts, so this
            // needs deterministic coordination, not a timing hope.
            //
            // Context A opens an explicit transaction and deletes the card but does NOT
            // commit yet. Postgres readers aren't blocked by an uncommitted DELETE (plain
            // MVCC reads), so context B's real DeleteAsync SELECT still finds the row -- but
            // B's own DELETE then blocks waiting for the row lock A is holding. Once A commits,
            // B's blocked DELETE re-evaluates its WHERE clause against the now-current (gone)
            // row, matches zero rows, and EF Core throws DbUpdateConcurrencyException --
            // exactly the real "two users click delete at the same instant" scenario, produced
            // deterministically instead of by chance.
            await using var transactionA = await dbA.Database.BeginTransactionAsync();
            var cardA = await dbA.Cards.FirstAsync(c => c.Id == card.Id);
            dbA.Cards.Remove(cardA);
            dbA.Entry(cardA).Property(c => c.Version).OriginalValue = originalVersion;
            await dbA.SaveChangesAsync(); // DELETE executed, not yet committed -- row lock held

            var taskB = serviceB.DeleteAsync(card.Id, originalVersion);

            // Generous margin for B's SELECT to run and its DELETE to reach the lock wait
            // before A's commit unblocks it -- not for correctness of the assertion itself
            // (that's the row lock, not this delay), only to keep the scenario deterministic.
            await Task.Delay(200);
            await transactionA.CommitAsync();

            var resultB = await taskB;
            Assert.IsType<DeleteCardResult.Success>(resultB);

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
            cleanupDb.BoardColumns.RemoveRange(cleanupDb.BoardColumns.Where(c => c.Id == column.Id));
            await cleanupDb.SaveChangesAsync();
        }
    }

    private const string HubPath = "/hubs/board";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FairNegativeWait = TimeSpan.FromSeconds(2);

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

    private async Task DeleteCardIfPresentAsync(Guid cardId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var card = await db.Cards.FindAsync(cardId);
        if (card is not null)
        {
            db.Cards.Remove(card);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task DeleteCard_SuccessfulDelete_BroadcastsBareCardIdToASecondJoinedConnection()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient, "Alice");
        var columnId = await GetSeededColumnIdAsync("Backlog");
        var card = await AddCardAsync(columnId, 500, "Card to delete");

        await using var deleter = await ConnectedAndJoinedAsync(BuildConnection(token));
        await using var otherConnection = await ConnectedAndJoinedAsync(BuildConnection(token));

        var otherReceived = new TaskCompletionSource<Guid>();
        otherConnection.On<Guid>("CardDeleted", id => otherReceived.TrySetResult(id));

        try
        {
            await deleter.InvokeAsync("DeleteCard", new DeleteCardRequest(card.Id, card.Version));

            var otherCompleted = await Task.WhenAny(otherReceived.Task, Task.Delay(WaitTimeout));
            Assert.Same(otherReceived.Task, otherCompleted);
            var otherId = await otherReceived.Task;

            Assert.Equal(card.Id, otherId);
        }
        finally
        {
            await DeleteCardIfPresentAsync(card.Id);
        }
    }

    [Fact]
    public async Task DeleteCard_UnaffiliatedConnection_DoesNotReceiveTheBroadcast()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient, "Alice");
        var columnId = await GetSeededColumnIdAsync("Backlog");
        var card = await AddCardAsync(columnId, 500, "Card to delete");

        await using var deleter = await ConnectedAndJoinedAsync(BuildConnection(token));

        // Connects and authenticates, but never calls JoinBoard, so it never joins the board's
        // group and should never receive the broadcast.
        await using var unaffiliatedConnection = BuildConnection(token);
        var unaffiliatedReceived = new TaskCompletionSource<Guid>();
        unaffiliatedConnection.On<Guid>("CardDeleted", id => unaffiliatedReceived.TrySetResult(id));
        await unaffiliatedConnection.StartAsync();

        var deleterReceived = new TaskCompletionSource<Guid>();
        deleter.On<Guid>("CardDeleted", id => deleterReceived.TrySetResult(id));

        try
        {
            await deleter.InvokeAsync("DeleteCard", new DeleteCardRequest(card.Id, card.Version));

            var deleterCompleted = await Task.WhenAny(deleterReceived.Task, Task.Delay(WaitTimeout));
            Assert.Same(deleterReceived.Task, deleterCompleted);

            var unaffiliatedCompleted = await Task.WhenAny(unaffiliatedReceived.Task, Task.Delay(FairNegativeWait));
            Assert.NotSame(unaffiliatedReceived.Task, unaffiliatedCompleted);
        }
        finally
        {
            await DeleteCardIfPresentAsync(card.Id);
        }
    }

    [Fact]
    public async Task DeleteCard_StaleVersion_RejectsCallerOnlyWithAuthoritativeStateAndDoesNotBroadcastToGroup()
    {
        var httpClient = factory.CreateClient();
        var deleterToken = await RegisterAndGetTokenAsync(httpClient, "Ahmed");
        var bystanderToken = await RegisterAndGetTokenAsync(httpClient, "Sara");
        var columnId = await GetSeededColumnIdAsync("Backlog");
        var otherColumnId = await GetSeededColumnIdAsync("In Progress");
        var card = await AddCardAsync(columnId, 500, "Card to delete");
        var originalVersion = card.Version;

        await using var deleter = await ConnectedAndJoinedAsync(BuildConnection(deleterToken));
        await using var bystander = await ConnectedAndJoinedAsync(BuildConnection(bystanderToken));

        var bystanderDeletedCount = 0;
        bystander.On<Guid>("CardDeleted", _ => Interlocked.Increment(ref bystanderDeletedCount));
        var bystanderRejected = new TaskCompletionSource<MoveRejectedDto>();
        bystander.On<MoveRejectedDto>("MoveRejected", dto => bystanderRejected.TrySetResult(dto));

        try
        {
            // A move lands first and bumps the card's real version out from under the stale
            // delete request.
            var firstMoved = new TaskCompletionSource<CardMovedDto>();
            deleter.On<CardMovedDto>("CardMoved", dto => firstMoved.TrySetResult(dto));
            await deleter.InvokeAsync(
                "MoveCard",
                new MoveCardRequest(card.Id, otherColumnId, AfterCardId: null, BeforeCardId: null, originalVersion)
            );
            var firstCompleted = await Task.WhenAny(firstMoved.Task, Task.Delay(WaitTimeout));
            Assert.Same(firstMoved.Task, firstCompleted);

            // The delete request reuses the now-stale original version.
            var rejected = new TaskCompletionSource<MoveRejectedDto>();
            deleter.On<MoveRejectedDto>("MoveRejected", dto => rejected.TrySetResult(dto));
            await deleter.InvokeAsync("DeleteCard", new DeleteCardRequest(card.Id, originalVersion));

            var rejectedCompleted = await Task.WhenAny(rejected.Task, Task.Delay(WaitTimeout));
            Assert.Same(rejected.Task, rejectedCompleted);
            var rejectedDto = await rejected.Task;

            Assert.Equal(nameof(RejectReason.StaleVersion), rejectedDto.Reason);
            Assert.Equal(card.Id, rejectedDto.CardId);
            Assert.NotNull(rejectedDto.Card);
            Assert.Equal(otherColumnId, rejectedDto.Card!.ColumnId);
            Assert.Equal("Ahmed", rejectedDto.WinnerDisplayName);

            // The bystander saw nothing from the rejected delete attempt: no MoveRejected
            // (caller-only) and no CardDeleted broadcast.
            var bystanderRejectedCompleted = await Task.WhenAny(bystanderRejected.Task, Task.Delay(FairNegativeWait));
            Assert.NotSame(bystanderRejected.Task, bystanderRejectedCompleted);
            Assert.Equal(0, bystanderDeletedCount);
        }
        finally
        {
            await DeleteCardIfPresentAsync(card.Id);
        }
    }
}
