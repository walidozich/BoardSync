using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BoardSync.Api.Data;
using BoardSync.Api.Features.Board;
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
public sealed class BoardHub(AppDbContext db, PresenceTracker presence, CardService cardService) : Hub
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

    // Broadcasts to the whole group, including the card's own creator: this phase doesn't do
    // optimistic client-side updates (that's MoveCard's job later, where drag demands instant
    // feedback), so the creator's own UI waits for the same broadcast as everyone else.
    public async Task CreateCard(CreateCardRequest request)
    {
        var result = await cardService.CreateAsync(request.ColumnId, request.Title, request.Description);

        switch (result)
        {
            case CreateCardResult.Success success:
                var boardId = await BoardQueries.GetBoardIdAsync(db);
                if (boardId is null)
                {
                    // no board seeded — shouldn't happen, since the card was just created
                    // against an existing column, which itself requires an existing board via FK
                    break;
                }

                var dto = new CardCreatedDto(
                    success.Card.Id,
                    success.Card.ColumnId,
                    success.Card.Title,
                    success.Card.Description,
                    success.Card.Position,
                    success.Card.Version
                );
                await Clients.Group(boardId.Value.ToString()).SendAsync("CardCreated", dto);
                break;
            case CreateCardResult.ValidationFailed failed:
                await Clients.Caller.SendAsync(
                    "CreateCardRejected",
                    new CreateCardRejectedDto(nameof(RejectReason.Invalid), failed.Errors)
                );
                break;
            case CreateCardResult.ColumnNotFound:
                await Clients.Caller.SendAsync(
                    "CreateCardRejected",
                    new CreateCardRejectedDto(nameof(RejectReason.ColumnNotFound), null)
                );
                break;
            case CreateCardResult.BoardFull:
                await Clients.Caller.SendAsync(
                    "CreateCardRejected",
                    new CreateCardRejectedDto(nameof(RejectReason.BoardFull), null)
                );
                break;
        }
    }

    // Broadcasts to the whole group, including whoever dragged the card -- same reasoning as
    // CreateCard: this phase's client applies the move optimistically on drop for instant
    // feedback, then reconciles against this same broadcast when it arrives, exactly like
    // everyone else. No special-casing for the mover.
    public async Task MoveCard(MoveCardRequest request)
    {
        var result = await cardService.MoveAsync(
            request.CardId,
            request.TargetColumnId,
            request.AfterCardId,
            request.BeforeCardId,
            request.ExpectedVersion
        );

        switch (result)
        {
            case MoveCardResult.Success success:
                var boardId = await BoardQueries.GetBoardIdAsync(db);
                if (boardId is null)
                {
                    break; // shouldn't happen -- the move itself required an existing board via FK
                }

                var dto = new CardMovedDto(
                    success.Card.Id,
                    success.Card.ColumnId,
                    success.Card.Position,
                    success.Card.Version
                );
                await Clients.Group(boardId.Value.ToString()).SendAsync("CardMoved", dto);
                break;
            case MoveCardResult.CardNotFound:
                await Clients.Caller.SendAsync(
                    "MoveRejected",
                    new MoveRejectedDto(nameof(RejectReason.CardNotFound), request.CardId)
                );
                break;
            case MoveCardResult.ColumnNotFound:
                await Clients.Caller.SendAsync(
                    "MoveRejected",
                    new MoveRejectedDto(nameof(RejectReason.ColumnNotFound), request.CardId)
                );
                break;
        }
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
