using BoardSync.Api.Data;
using BoardSync.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Features.Board;

public abstract record CreateCardResult
{
    public sealed record Success(Card Card) : CreateCardResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : CreateCardResult;

    public sealed record ColumnNotFound : CreateCardResult;

    public sealed record BoardFull : CreateCardResult;
}

/// <summary>
/// Not exhaustively matched anywhere outside BoardHub's own switch -- a future phase adds
/// StaleVersion and CardDeleted cases here once optimistic-concurrency enforcement lands, and
/// that addition should only ever require touching the hub's switch, not any other consumer.
/// </summary>
public abstract record MoveCardResult
{
    public sealed record Success(Card Card) : MoveCardResult;

    public sealed record CardNotFound : MoveCardResult;

    public sealed record ColumnNotFound : MoveCardResult;
}

public enum RejectReason
{
    Invalid,
    ColumnNotFound,
    BoardFull,
    CardNotFound,
}

/// <summary>
/// All card mutation logic lives here, independent of SignalR: it takes plain values in and
/// returns a typed result out, never touching Hub/Clients/anything hub-related. This is what
/// lets a future phase's concurrency-conflict logic race two DbContext instances directly
/// against this class, with no SignalR in the loop at all. MoveCard and DeleteCard (later
/// phases) extend this same class and follow the same result-type pattern.
/// </summary>
public sealed class CardService(AppDbContext db)
{
    private const int MaxCardsOnBoard = 200;
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 2000;

    public async Task<CreateCardResult> CreateAsync(Guid columnId, string title, string? description)
    {
        var errors = new Dictionary<string, string[]>();

        var trimmedTitle = title?.Trim() ?? string.Empty;
        if (trimmedTitle.Length is 0 or > MaxTitleLength)
        {
            errors[nameof(CreateCardRequest.Title)] =
                [$"Title must be between 1 and {MaxTitleLength} characters."];
        }

        // Trimmed the same way Title is; an empty-after-trim description is treated as "no
        // description" rather than stored as a blank string.
        var trimmedDescription = description?.Trim();
        if (trimmedDescription is { Length: 0 })
        {
            trimmedDescription = null;
        }

        if (trimmedDescription is { Length: > MaxDescriptionLength })
        {
            errors[nameof(CreateCardRequest.Description)] =
                [$"Description must be at most {MaxDescriptionLength} characters."];
        }

        if (errors.Count > 0)
        {
            return new CreateCardResult.ValidationFailed(errors);
        }

        var columnExists = await db.BoardColumns.AsNoTracking().AnyAsync(c => c.Id == columnId);
        if (!columnExists)
        {
            return new CreateCardResult.ColumnNotFound();
        }

        // Exactly one board, permanently -- "cards on the board" and "every row in the cards
        // table" are the same count, so this doesn't need to join through columns to a board id.
        var totalCards = await db.Cards.CountAsync();
        if (totalCards >= MaxCardsOnBoard)
        {
            return new CreateCardResult.BoardFull();
        }

        var position = await BottomOfColumnPositionAsync(columnId);

        var card = new Card
        {
            ColumnId = columnId,
            Title = trimmedTitle,
            Description = trimmedDescription,
            Position = position,
        };

        db.Cards.Add(card);
        await db.SaveChangesAsync();

        return new CreateCardResult.Success(card);
    }

    public async Task<MoveCardResult> MoveAsync(
        Guid cardId,
        Guid targetColumnId,
        Guid? afterCardId,
        Guid? beforeCardId,
        // Accepted now but not yet enforced -- the concurrency-conflict check (comparing this
        // against the card's stored Version and rejecting stale moves) lands next phase. Taking
        // it in the signature now means only the implementation changes then, not the contract.
        uint expectedVersion
    )
    {
        var card = await db.Cards.FirstOrDefaultAsync(c => c.Id == cardId);
        if (card is null)
        {
            return new MoveCardResult.CardNotFound();
        }

        var columnExists = await db.BoardColumns.AsNoTracking().AnyAsync(c => c.Id == targetColumnId);
        if (!columnExists)
        {
            return new MoveCardResult.ColumnNotFound();
        }

        var afterCard =
            afterCardId is { } afterId ? await db.Cards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == afterId) : null;
        var afterCardValid = afterCardId is null || (afterCard is not null && afterCard.ColumnId == targetColumnId);

        var beforeCard =
            beforeCardId is { } beforeId
                ? await db.Cards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == beforeId)
                : null;
        var beforeCardValid = beforeCardId is null || (beforeCard is not null && beforeCard.ColumnId == targetColumnId);

        double position;
        if (!afterCardValid || !beforeCardValid)
        {
            // A named neighbour vanished from under the request (deleted, or moved elsewhere by
            // someone else since this client last saw the board) -- trust the server's current
            // state over the client's stale neighbour info and fall back to the bottom of the
            // column, excluding the card being moved itself so a same-column reorder doesn't let
            // the card's own prior position skew the "current max" it's being positioned against.
            position = await BottomOfColumnPositionAsync(targetColumnId, excludeCardId: cardId);
        }
        else if (afterCard is not null && beforeCard is not null)
        {
            position = (afterCard.Position + beforeCard.Position) / 2;
        }
        else if (afterCard is null && beforeCard is not null)
        {
            position = beforeCard.Position / 2;
        }
        else if (afterCard is not null && beforeCard is null)
        {
            position = afterCard.Position + 1000;
        }
        else
        {
            position = 1000;
        }

        card.ColumnId = targetColumnId;
        card.Position = position;
        await db.SaveChangesAsync();

        return new MoveCardResult.Success(card);
    }

    /// <summary>
    /// Position 1000 below the current highest position in the column, or 1000 if the column is
    /// empty. Shared by CreateAsync (new cards always land at the bottom) and MoveAsync's
    /// stale-neighbour fallback. excludeCardId lets MoveAsync exclude the card being moved from
    /// its own "current max" calculation -- otherwise reordering a card within its own column
    /// could compute a new position relative to its own prior position.
    /// </summary>
    private async Task<double> BottomOfColumnPositionAsync(Guid columnId, Guid? excludeCardId = null)
    {
        var query = db.Cards.Where(c => c.ColumnId == columnId);
        if (excludeCardId is { } exclude)
        {
            query = query.Where(c => c.Id != exclude);
        }

        var maxPosition = await query.Select(c => (double?)c.Position).MaxAsync();
        return (maxPosition ?? 0) + 1000;
    }
}
