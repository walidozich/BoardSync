using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BoardSync.Api.Features.Auth;
using BoardSync.Api.Features.Board;
using BoardSync.Api.Hubs;
using BoardSync.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace BoardSync.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class BoardHubTests(BoardSyncApiFactory factory)
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

    // The in-memory TestServer doesn't support real WebSocket upgrades, so the test transport is
    // LongPolling — this is the documented pattern for exercising a SignalR hub against
    // WebApplicationFactory. The access token (when present) travels in the query string, exactly
    // as a real browser client would send it, since it can't set custom headers on the handshake.
    private HubConnection BuildConnection(string? accessToken)
    {
        var url = accessToken is null ? HubPath : $"{HubPath}?access_token={accessToken}";

        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress, url),
                HttpTransportType.LongPolling,
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                }
            )
            .Build();
    }

    [Fact]
    public async Task Connection_WithNoToken_IsRefusedAtHandshake()
    {
        await using var connection = BuildConnection(accessToken: null);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task Connection_WithValidToken_CanJoinBoardAndReceivesMatchingSnapshot()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient);

        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var restResponse = await httpClient.GetAsync("/api/board");
        var restBoard = await restResponse.Content.ReadFromJsonAsync<BoardStateDto>();
        Assert.NotNull(restBoard);

        await using var connection = BuildConnection(token);

        var snapshotReceived = new TaskCompletionSource<BoardStateDto>();
        connection.On<BoardStateDto>("BoardSnapshot", snapshot => snapshotReceived.TrySetResult(snapshot));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinBoard");

        var completed = await Task.WhenAny(snapshotReceived.Task, Task.Delay(WaitTimeout));
        Assert.Same(snapshotReceived.Task, completed);

        var hubBoard = await snapshotReceived.Task;

        Assert.Equal(restBoard.Id, hubBoard.Id);
        Assert.Equal(restBoard.Columns.Count, hubBoard.Columns.Count);
        Assert.Equal(
            restBoard.Columns.Sum(c => c.Cards.Count),
            hubBoard.Columns.Sum(c => c.Cards.Count)
        );
    }

    [Fact]
    public async Task QueryStringToken_IsNotAcceptedOnRestRoute()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient);

        // No Authorization header — the token travels only via the query string, exactly the
        // shape OnMessageReceived would need to accept for this to become a REST-usable auth
        // path. It must not be: the query-string fallback is wired to /hubs/board only.
        var response = await httpClient.GetAsync($"/api/board?access_token={token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnaffiliatedConnection_DoesNotReceiveTrafficSentToTheJoinedGroup()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient);

        // Joins the real board's group.
        await using var joinedConnection = BuildConnection(token);
        var snapshotReceived = new TaskCompletionSource<BoardStateDto>();
        joinedConnection.On<BoardStateDto>("BoardSnapshot", s => snapshotReceived.TrySetResult(s));
        await joinedConnection.StartAsync();
        await joinedConnection.InvokeAsync("JoinBoard");

        var joinCompleted = await Task.WhenAny(snapshotReceived.Task, Task.Delay(WaitTimeout));
        Assert.Same(snapshotReceived.Task, joinCompleted);
        var boardId = (await snapshotReceived.Task).Id;

        // Connects and authenticates, but never calls JoinBoard, so it never joins the board's
        // group.
        await using var unaffiliatedConnection = BuildConnection(token);
        var unaffiliatedReceived = new TaskCompletionSource<string>();
        unaffiliatedConnection.On<string>("GroupPing", msg => unaffiliatedReceived.TrySetResult(msg));
        await unaffiliatedConnection.StartAsync();

        var joinedReceivedPing = new TaskCompletionSource<string>();
        joinedConnection.On<string>("GroupPing", msg => joinedReceivedPing.TrySetResult(msg));

        // Broadcasts directly to the board's group from outside any hub method, so this proves
        // group membership itself is isolated rather than relying on JoinBoard's own
        // caller-only send.
        var hubContext = factory.Services.GetRequiredService<IHubContext<BoardHub>>();
        await hubContext.Clients.Group(boardId.ToString()).SendAsync("GroupPing", "hello");

        var joinedPingCompleted = await Task.WhenAny(joinedReceivedPing.Task, Task.Delay(WaitTimeout));
        Assert.Same(joinedReceivedPing.Task, joinedPingCompleted);
        Assert.Equal("hello", await joinedReceivedPing.Task);

        // Fair window for the unaffiliated connection to (wrongly) receive the same broadcast.
        var unaffiliatedCompleted = await Task.WhenAny(
            unaffiliatedReceived.Task,
            Task.Delay(FairNegativeWait)
        );
        Assert.NotSame(unaffiliatedReceived.Task, unaffiliatedCompleted);
    }
}
