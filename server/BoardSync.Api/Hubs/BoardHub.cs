using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BoardSync.Api.Data;
using BoardSync.Api.Presence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BoardSync.Api.Hubs;

/// <summary>
/// Real-time board hub. With exactly one board seeded and multi-board management
/// permanently out of scope, JoinBoard takes no parameters -- the hub resolves the one
/// seeded board itself, the same way GET /api/board already does. (Deliberate,
/// documented correction to the original spec, already reflected in spec.md.)
///
/// Presence is tracked per connection to the hub, not per board group: OnConnectedAsync
/// fires before the client has called JoinBoard, so group membership isn't established
/// yet at that point. With exactly one board, "everyone connected to the hub" and
/// "everyone viewing the board" are the same population, so PresenceChanged broadcasts
/// to all connected clients rather than a specific group.
/// </summary>
[Authorize]
public sealed class BoardHub(AppDbContext db, PresenceTracker presence) : Hub
{
    public async Task JoinBoard()
    {
        var snapshot = await BoardQueries.GetBoardStateAsync(db);
        if (snapshot is null)
        {
            return; // no board seeded — shouldn't happen outside a broken environment
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, snapshot.Id.ToString());
        await Clients.Caller.SendAsync("BoardSnapshot", snapshot);
    }

    public override async Task OnConnectedAsync()
    {
        var isNewUser = presence.AddConnection(GetUserId(), GetDisplayName(), Context.ConnectionId);
        var roster = presence.ConnectedUsers();

        // Always tell the connecting client the current roster, even when they're not a new
        // user (a second tab) or didn't change it at all -- otherwise a client that connects
        // after others are already present only ever learns about roster changes from that
        // point on, never who's already there.
        await Clients.Caller.SendAsync("PresenceChanged", roster);
        if (isNewUser)
        {
            await Clients.Others.SendAsync("PresenceChanged", roster);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var wasLastConnection = presence.RemoveConnection(GetUserId(), Context.ConnectionId);
        if (wasLastConnection)
        {
            await Clients.All.SendAsync("PresenceChanged", presence.ConnectedUsers());
        }

        await base.OnDisconnectedAsync(exception);
    }

    // JWT inbound claim mapping (short "sub"/"name" vs. long ClaimTypes URIs) has changed
    // defaults across ASP.NET Core versions; check both rather than assume one.
    private Guid GetUserId()
    {
        var claim =
            Context.User?.FindFirst(JwtRegisteredClaimNames.Sub)
            ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier);
        return Guid.Parse(claim!.Value);
    }

    private string GetDisplayName()
    {
        var claim =
            Context.User?.FindFirst(JwtRegisteredClaimNames.Name) ?? Context.User?.FindFirst(ClaimTypes.Name);
        return claim!.Value;
    }
}
