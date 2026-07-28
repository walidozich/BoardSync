using System.Net.Http.Json;
using BoardSync.Api.Features.Auth;
using BoardSync.Api.Presence;
using BoardSync.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace BoardSync.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class PresenceTests(BoardSyncApiFactory factory)
{
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

    // Same LongPolling-over-TestServer pattern as BoardHubTests — the in-memory TestServer
    // doesn't support real WebSocket upgrades.
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

    private static async Task<IReadOnlyList<PresenceUser>> WaitForNextPresenceAsync(HubConnection connection)
    {
        var received = new TaskCompletionSource<IReadOnlyList<PresenceUser>>();
        using var subscription = connection.On<IReadOnlyList<PresenceUser>>(
            "PresenceChanged",
            roster => received.TrySetResult(roster)
        );

        var completed = await Task.WhenAny(received.Task, Task.Delay(WaitTimeout));
        Assert.Same(received.Task, completed);
        return await received.Task;
    }

    [Fact]
    public async Task Connecting_ReceivesTheCurrentRosterImmediately()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient, "Alice");

        await using var connection = BuildConnection(token);
        var presenceTask = WaitForNextPresenceAsync(connection);

        await connection.StartAsync();

        var roster = await presenceTask;
        Assert.Contains(roster, u => u.DisplayName == "Alice");
    }

    [Fact]
    public async Task TwoConnectionsForTheSameUser_AppearOnceInTheRoster()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient, "Bob");

        await using var firstConnection = BuildConnection(token);
        var firstPresence = WaitForNextPresenceAsync(firstConnection);
        await firstConnection.StartAsync();
        await firstPresence;

        // A second connection authenticated as the same user (e.g. a second browser tab).
        await using var secondConnection = BuildConnection(token);
        var secondPresence = WaitForNextPresenceAsync(secondConnection);
        await secondConnection.StartAsync();
        var roster = await secondPresence;

        Assert.Single(roster, u => u.DisplayName == "Bob");
    }

    [Fact]
    public async Task ClosingOneOfTwoConnections_DoesNotRemoveTheUser()
    {
        var httpClient = factory.CreateClient();
        var token = await RegisterAndGetTokenAsync(httpClient, "Carol");

        var firstConnection = BuildConnection(token);
        var firstPresence = WaitForNextPresenceAsync(firstConnection);
        await firstConnection.StartAsync();
        await firstPresence;

        await using var secondConnection = BuildConnection(token);
        var secondPresence = WaitForNextPresenceAsync(secondConnection);
        await secondConnection.StartAsync();
        await secondPresence;

        // Closing the first (of two) connections must not trigger a "user left" broadcast —
        // Carol is still present via the second connection. Assert no PresenceChanged
        // arrives on the survivor within a fair window.
        var unexpectedPresence = WaitForNextPresenceAsync(secondConnection);
        await firstConnection.DisposeAsync();

        var completed = await Task.WhenAny(unexpectedPresence, Task.Delay(FairNegativeWait));
        Assert.NotSame(unexpectedPresence, completed);
    }

    [Fact]
    public async Task ClosingTheLastConnection_RemovesTheUser()
    {
        var httpClient = factory.CreateClient();
        var watcherToken = await RegisterAndGetTokenAsync(httpClient, "Watcher");
        var subjectToken = await RegisterAndGetTokenAsync(httpClient, "Dana");

        // A separate connection to observe the broadcast Dana's disconnect triggers for
        // everyone else.
        await using var watcherConnection = BuildConnection(watcherToken);
        var watcherInitialPresence = WaitForNextPresenceAsync(watcherConnection);
        await watcherConnection.StartAsync();
        await watcherInitialPresence;

        var watcherSeesDanaJoin = WaitForNextPresenceAsync(watcherConnection);
        var subjectConnection = BuildConnection(subjectToken);
        await subjectConnection.StartAsync();
        var rosterAfterJoin = await watcherSeesDanaJoin;
        Assert.Contains(rosterAfterJoin, u => u.DisplayName == "Dana");

        var watcherSeesDanaLeave = WaitForNextPresenceAsync(watcherConnection);
        await subjectConnection.DisposeAsync();

        var rosterAfterLeave = await watcherSeesDanaLeave;
        Assert.DoesNotContain(rosterAfterLeave, u => u.DisplayName == "Dana");
    }
}
