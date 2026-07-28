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
public sealed class MoveCardHubTests(BoardSyncApiFactory factory)
{
    private const string HubPath = "/hubs/board";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FairNegativeWait = TimeSpan.FromSeconds(2);

    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"user-{Guid.NewGuid():N}@example.com", "correcthorse123", "Alice")
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
    public async Task MoveCard_SuccessfulMove_BroadcastReachesASecondJoinedConnection()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient);
        var columnId = await GetSeededColumnIdAsync("Backlog");
        var card = await AddCardAsync(columnId, 500, "Card to move");

        await using var mover = await ConnectedAndJoinedAsync(BuildConnection(token));
        await using var otherConnection = await ConnectedAndJoinedAsync(BuildConnection(token));

        var otherReceived = new TaskCompletionSource<CardMovedDto>();
        otherConnection.On<CardMovedDto>("CardMoved", dto => otherReceived.TrySetResult(dto));

        try
        {
            await mover.InvokeAsync(
                "MoveCard",
                new MoveCardRequest(card.Id, columnId, AfterCardId: null, BeforeCardId: null, ExpectedVersion: 0)
            );

            var otherCompleted = await Task.WhenAny(otherReceived.Task, Task.Delay(WaitTimeout));
            Assert.Same(otherReceived.Task, otherCompleted);
            var otherDto = await otherReceived.Task;

            Assert.Equal(card.Id, otherDto.Id);
            Assert.Equal(columnId, otherDto.ColumnId);
        }
        finally
        {
            await DeleteCardAsync(card.Id);
        }
    }

    [Fact]
    public async Task MoveCard_UnaffiliatedConnection_DoesNotReceiveTheBroadcast()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient);
        var columnId = await GetSeededColumnIdAsync("Backlog");
        var card = await AddCardAsync(columnId, 500, "Card to move");

        await using var mover = await ConnectedAndJoinedAsync(BuildConnection(token));

        // Connects and authenticates, but never calls JoinBoard, so it never joins the board's
        // group and should never receive the broadcast.
        await using var unaffiliatedConnection = BuildConnection(token);
        var unaffiliatedReceived = new TaskCompletionSource<CardMovedDto>();
        unaffiliatedConnection.On<CardMovedDto>("CardMoved", dto => unaffiliatedReceived.TrySetResult(dto));
        await unaffiliatedConnection.StartAsync();

        var moverReceived = new TaskCompletionSource<CardMovedDto>();
        mover.On<CardMovedDto>("CardMoved", dto => moverReceived.TrySetResult(dto));

        try
        {
            await mover.InvokeAsync(
                "MoveCard",
                new MoveCardRequest(card.Id, columnId, AfterCardId: null, BeforeCardId: null, ExpectedVersion: 0)
            );

            var moverCompleted = await Task.WhenAny(moverReceived.Task, Task.Delay(WaitTimeout));
            Assert.Same(moverReceived.Task, moverCompleted);

            var unaffiliatedCompleted = await Task.WhenAny(unaffiliatedReceived.Task, Task.Delay(FairNegativeWait));
            Assert.NotSame(unaffiliatedReceived.Task, unaffiliatedCompleted);
        }
        finally
        {
            await DeleteCardAsync(card.Id);
        }
    }

    [Fact]
    public async Task MoveCard_UnknownCardId_RejectsCallerOnlyAndDoesNotBroadcast()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient);
        var columnId = await GetSeededColumnIdAsync("Backlog");

        await using var mover = await ConnectedAndJoinedAsync(BuildConnection(token));
        await using var otherConnection = await ConnectedAndJoinedAsync(BuildConnection(token));

        var rejected = new TaskCompletionSource<MoveRejectedDto>();
        mover.On<MoveRejectedDto>("MoveRejected", dto => rejected.TrySetResult(dto));

        var otherReceivedMoved = new TaskCompletionSource<CardMovedDto>();
        otherConnection.On<CardMovedDto>("CardMoved", dto => otherReceivedMoved.TrySetResult(dto));
        var moverReceivedMoved = new TaskCompletionSource<CardMovedDto>();
        mover.On<CardMovedDto>("CardMoved", dto => moverReceivedMoved.TrySetResult(dto));

        var unknownCardId = Guid.NewGuid();
        await mover.InvokeAsync(
            "MoveCard",
            new MoveCardRequest(unknownCardId, columnId, AfterCardId: null, BeforeCardId: null, ExpectedVersion: 0)
        );

        var rejectedCompleted = await Task.WhenAny(rejected.Task, Task.Delay(WaitTimeout));
        Assert.Same(rejected.Task, rejectedCompleted);

        var rejectedDto = await rejected.Task;
        Assert.Equal(nameof(RejectReason.CardNotFound), rejectedDto.Reason);
        Assert.Equal(unknownCardId, rejectedDto.CardId);

        // Fair window for either connection to (wrongly) receive a CardMoved broadcast.
        var otherCompleted = await Task.WhenAny(otherReceivedMoved.Task, Task.Delay(FairNegativeWait));
        Assert.NotSame(otherReceivedMoved.Task, otherCompleted);

        var moverCompleted = await Task.WhenAny(moverReceivedMoved.Task, Task.Delay(FairNegativeWait));
        Assert.NotSame(moverReceivedMoved.Task, moverCompleted);
    }
}
