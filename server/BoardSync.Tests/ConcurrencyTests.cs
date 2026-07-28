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

// The centerpiece of the whole project: everything else built up to this. Two independent
// DbContext instances (never one context racing itself, which EF Core's change tracker would
// never allow to happen this way) race to move the same card. This is the deterministic,
// real-database proof that the optimistic-concurrency check actually works -- not a mock, not
// a simulated exception, an actual `xmin`-mismatched UPDATE against a real Postgres row.
[Collection(DatabaseCollection.Name)]
public sealed class ConcurrencyTests(BoardSyncApiFactory factory)
{
    private const string HubPath = "/hubs/board";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FairNegativeWait = TimeSpan.FromSeconds(2);

    private static async Task<BoardColumn> CreateEmptyColumnAsync(AppDbContext db)
    {
        var board = await db.Boards.AsNoTracking().FirstAsync();
        var column = new BoardColumn { BoardId = board.Id, Name = $"Racing-{Guid.NewGuid():N}", Position = 9000 };
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
    public async Task MoveAsync_TwoDbContextsRaceOnTheSameCard_SecondIsRejectedAsStaleWithAuthoritativeState()
    {
        await using var seedScope = factory.Services.CreateAsyncScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var column = await CreateEmptyColumnAsync(seedDb);
        var otherColumn = await CreateEmptyColumnAsync(seedDb);
        var card = await AddCardAsync(seedDb, column.Id, 1000, "Racing card");
        var originalVersion = card.Version;

        // Two independent DI scopes -- each resolves its own AppDbContext (scoped) and its own
        // CardService wrapping it, exactly like two separate hub invocations from two separate
        // users would. Neither context knows about the other.
        await using var scopeA = factory.Services.CreateAsyncScope();
        var serviceA = scopeA.ServiceProvider.GetRequiredService<CardService>();

        await using var scopeB = factory.Services.CreateAsyncScope();
        var serviceB = scopeB.ServiceProvider.GetRequiredService<CardService>();

        try
        {
            // Ahmed's move lands first and wins.
            var resultA = await serviceA.MoveAsync(card.Id, otherColumn.Id, null, null, originalVersion, "Ahmed");
            var successA = Assert.IsType<MoveCardResult.Success>(resultA);
            Assert.Equal(otherColumn.Id, successA.Card.ColumnId);

            // Sara's request still carries the *same* originalVersion she read before either
            // move happened -- exactly the real race: her request was in flight while Ahmed's
            // landed. This must be caught as DbUpdateConcurrencyException internally and
            // translated into MoveCardResult.StaleVersion, not silently overwrite Ahmed's move.
            var resultB = await serviceB.MoveAsync(card.Id, column.Id, null, null, originalVersion, "Sara");

            var staleB = Assert.IsType<MoveCardResult.StaleVersion>(resultB);
            Assert.Equal("Ahmed", staleB.WinnerDisplayName);
            Assert.Equal(otherColumn.Id, staleB.AuthoritativeCard.ColumnId);
            Assert.Equal(successA.Card.Position, staleB.AuthoritativeCard.Position);
            Assert.Equal(successA.Card.Version, staleB.AuthoritativeCard.Version);

            // The database itself reflects only Ahmed's write -- Sara's rejected attempt never
            // touched the row.
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await verifyDb.Cards.AsNoTracking().FirstAsync(c => c.Id == card.Id);
            Assert.Equal(otherColumn.Id, persisted.ColumnId);
            Assert.Equal("Ahmed", persisted.LastModifiedBy);
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

    private async Task DeleteCardAsync(Guid cardId)
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
    public async Task MoveCard_StaleVersion_RejectsCallerOnlyWithAuthoritativeStateAndDoesNotBroadcastToGroup()
    {
        var httpClient = factory.CreateClient();
        var moverToken = await RegisterAndGetTokenAsync(httpClient, "Ahmed");
        var bystanderToken = await RegisterAndGetTokenAsync(httpClient, "Sara");
        var columnId = await GetSeededColumnIdAsync("Backlog");
        var otherColumnId = await GetSeededColumnIdAsync("In Progress");
        var card = await AddCardAsync(columnId, 500, "Racing card");
        var originalVersion = card.Version;

        await using var mover = await ConnectedAndJoinedAsync(BuildConnection(moverToken));
        await using var bystander = await ConnectedAndJoinedAsync(BuildConnection(bystanderToken));

        var bystanderMovedCount = 0;
        bystander.On<CardMovedDto>("CardMoved", _ => Interlocked.Increment(ref bystanderMovedCount));
        var bystanderRejected = new TaskCompletionSource<MoveRejectedDto>();
        bystander.On<MoveRejectedDto>("MoveRejected", dto => bystanderRejected.TrySetResult(dto));

        try
        {
            // First move succeeds and bumps the card's real version.
            var firstMoved = new TaskCompletionSource<CardMovedDto>();
            mover.On<CardMovedDto>("CardMoved", dto => firstMoved.TrySetResult(dto));
            await mover.InvokeAsync(
                "MoveCard",
                new MoveCardRequest(card.Id, otherColumnId, AfterCardId: null, BeforeCardId: null, originalVersion)
            );
            var firstCompleted = await Task.WhenAny(firstMoved.Task, Task.Delay(WaitTimeout));
            Assert.Same(firstMoved.Task, firstCompleted);

            // Second attempt reuses the now-stale original version -- the same "in-flight
            // request built against an older snapshot" scenario as the real race.
            var rejected = new TaskCompletionSource<MoveRejectedDto>();
            mover.On<MoveRejectedDto>("MoveRejected", dto => rejected.TrySetResult(dto));
            await mover.InvokeAsync(
                "MoveCard",
                new MoveCardRequest(card.Id, columnId, AfterCardId: null, BeforeCardId: null, originalVersion)
            );

            var rejectedCompleted = await Task.WhenAny(rejected.Task, Task.Delay(WaitTimeout));
            Assert.Same(rejected.Task, rejectedCompleted);
            var rejectedDto = await rejected.Task;

            Assert.Equal(nameof(RejectReason.StaleVersion), rejectedDto.Reason);
            Assert.Equal(card.Id, rejectedDto.CardId);
            Assert.NotNull(rejectedDto.Card);
            Assert.Equal(otherColumnId, rejectedDto.Card!.ColumnId);
            Assert.Equal("Ahmed", rejectedDto.WinnerDisplayName);

            // The bystander saw exactly the one legitimate move and nothing from the rejected
            // second attempt: no MoveRejected (that's caller-only) and no extra CardMoved.
            var bystanderRejectedCompleted = await Task.WhenAny(bystanderRejected.Task, Task.Delay(FairNegativeWait));
            Assert.NotSame(bystanderRejected.Task, bystanderRejectedCompleted);
            Assert.Equal(1, bystanderMovedCount);
        }
        finally
        {
            await DeleteCardAsync(card.Id);
        }
    }
}
