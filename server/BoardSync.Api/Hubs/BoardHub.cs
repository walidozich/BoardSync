using BoardSync.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BoardSync.Api.Hubs;

/// <summary>
/// Real-time board hub. With exactly one board seeded and multi-board management
/// permanently out of scope, JoinBoard takes no parameters -- the hub resolves the one
/// seeded board itself, the same way GET /api/board already does. (Deliberate,
/// documented correction to the original spec, already reflected in spec.md.)
/// </summary>
[Authorize]
public sealed class BoardHub(AppDbContext db) : Hub
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
}
