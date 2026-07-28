using System.Net.Http.Headers;
using System.Net.Http.Json;
using BoardSync.Api.Data;
using BoardSync.Api.Features.Auth;
using BoardSync.Api.Features.Board;
using BoardSync.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoardSync.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class CreateCardHubTests(BoardSyncApiFactory factory)
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

    // Same LongPolling-transport pattern as BoardHubTests: the in-memory TestServer doesn't
    // support real WebSocket upgrades.
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
    public async Task CreateCard_BroadcastReachesASecondJoinedConnection_NotJustTheCreator()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient);
        var columnId = await GetSeededColumnIdAsync("Backlog");

        await using var creator = await ConnectedAndJoinedAsync(BuildConnection(token));
        await using var otherConnection = await ConnectedAndJoinedAsync(BuildConnection(token));

        var otherReceived = new TaskCompletionSource<CardCreatedDto>();
        otherConnection.On<CardCreatedDto>("CardCreated", dto => otherReceived.TrySetResult(dto));

        var creatorReceived = new TaskCompletionSource<CardCreatedDto>();
        creator.On<CardCreatedDto>("CardCreated", dto => creatorReceived.TrySetResult(dto));

        Guid? createdCardId = null;
        try
        {
            await creator.InvokeAsync("CreateCard", new CreateCardRequest(columnId, "Broadcast test card", null));

            var otherCompleted = await Task.WhenAny(otherReceived.Task, Task.Delay(WaitTimeout));
            Assert.Same(otherReceived.Task, otherCompleted);
            var otherDto = await otherReceived.Task;
            createdCardId = otherDto.Id;

            Assert.Equal(columnId, otherDto.ColumnId);
            Assert.Equal("Broadcast test card", otherDto.Title);
            Assert.NotEqual(0u, otherDto.Version);

            // Deliberate: the creator gets the same broadcast as everyone else, no optimistic
            // client-side update in this phase.
            var creatorCompleted = await Task.WhenAny(creatorReceived.Task, Task.Delay(WaitTimeout));
            Assert.Same(creatorReceived.Task, creatorCompleted);
            Assert.Equal(otherDto.Id, (await creatorReceived.Task).Id);
        }
        finally
        {
            if (createdCardId is { } id)
            {
                await DeleteCardAsync(id);
            }
        }
    }

    [Fact]
    public async Task CreateCard_UnaffiliatedConnection_DoesNotReceiveTheBroadcast()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient);
        var columnId = await GetSeededColumnIdAsync("Backlog");

        await using var creator = await ConnectedAndJoinedAsync(BuildConnection(token));

        // Connects and authenticates, but never calls JoinBoard, so it never joins the board's
        // group and should never receive the broadcast.
        await using var unaffiliatedConnection = BuildConnection(token);
        var unaffiliatedReceived = new TaskCompletionSource<CardCreatedDto>();
        unaffiliatedConnection.On<CardCreatedDto>("CardCreated", dto => unaffiliatedReceived.TrySetResult(dto));
        await unaffiliatedConnection.StartAsync();

        var creatorReceived = new TaskCompletionSource<CardCreatedDto>();
        creator.On<CardCreatedDto>("CardCreated", dto => creatorReceived.TrySetResult(dto));

        Guid? createdCardId = null;
        try
        {
            await creator.InvokeAsync(
                "CreateCard",
                new CreateCardRequest(columnId, "Unaffiliated isolation test card", null)
            );

            var creatorCompleted = await Task.WhenAny(creatorReceived.Task, Task.Delay(WaitTimeout));
            Assert.Same(creatorReceived.Task, creatorCompleted);
            createdCardId = (await creatorReceived.Task).Id;

            var unaffiliatedCompleted = await Task.WhenAny(unaffiliatedReceived.Task, Task.Delay(FairNegativeWait));
            Assert.NotSame(unaffiliatedReceived.Task, unaffiliatedCompleted);
        }
        finally
        {
            if (createdCardId is { } id)
            {
                await DeleteCardAsync(id);
            }
        }
    }

    [Fact]
    public async Task CreateCard_EmptyTitle_RejectsCallerOnlyAndDoesNotBroadcast()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient);
        var columnId = await GetSeededColumnIdAsync("Backlog");

        await using var creator = await ConnectedAndJoinedAsync(BuildConnection(token));
        await using var otherConnection = await ConnectedAndJoinedAsync(BuildConnection(token));

        var rejected = new TaskCompletionSource<CreateCardRejectedDto>();
        creator.On<CreateCardRejectedDto>("CreateCardRejected", dto => rejected.TrySetResult(dto));

        var otherReceivedCreated = new TaskCompletionSource<CardCreatedDto>();
        otherConnection.On<CardCreatedDto>("CardCreated", dto => otherReceivedCreated.TrySetResult(dto));
        var creatorReceivedCreated = new TaskCompletionSource<CardCreatedDto>();
        creator.On<CardCreatedDto>("CardCreated", dto => creatorReceivedCreated.TrySetResult(dto));

        await creator.InvokeAsync("CreateCard", new CreateCardRequest(columnId, "   ", null));

        var rejectedCompleted = await Task.WhenAny(rejected.Task, Task.Delay(WaitTimeout));
        Assert.Same(rejected.Task, rejectedCompleted);

        var rejectedDto = await rejected.Task;
        Assert.Equal(nameof(RejectReason.Invalid), rejectedDto.Reason);
        Assert.NotNull(rejectedDto.Errors);
        Assert.NotEmpty(rejectedDto.Errors!);

        // Fair window for either connection to (wrongly) receive a CardCreated broadcast.
        var otherCompleted = await Task.WhenAny(otherReceivedCreated.Task, Task.Delay(FairNegativeWait));
        Assert.NotSame(otherReceivedCreated.Task, otherCompleted);

        var creatorCompleted = await Task.WhenAny(creatorReceivedCreated.Task, Task.Delay(FairNegativeWait));
        Assert.NotSame(creatorReceivedCreated.Task, creatorCompleted);
    }
}
