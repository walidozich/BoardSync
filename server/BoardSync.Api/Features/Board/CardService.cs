using BoardSync.Api.Data;
using BoardSync.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BoardSync.Api.Features.Board;

public abstract record CreateCardResult
{
    public sealed record Success(Card Card) : CreateCardResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : CreateCardResult;

    public sealed record ColumnNotFound : CreateCardResult;

    public sealed record BoardFull : CreateCardResult;
}

/// <summary>
/// Not exhaustively matched anywhere outside BoardHub's own switch. Delete is a distinct
/// operation with its own DeleteCardResult below rather than a case added here, since a
/// deleted card was never "moved" -- CreateCard/MoveCard/DeleteCard each get their own result
/// type in this same style.
/// </summary>
public abstract record MoveCardResult
{
    /// <summary>
    /// Renormalized is true when this move also rewrote every card's position in the target
    /// column (see RenormalizeColumnAsync) -- that changes every one of those cards' xmin, so
    /// the hub must broadcast a full BoardSnapshot instead of a single CardMoved for this case.
    /// </summary>
    public sealed record Success(Card Card, bool Renormalized) : MoveCardResult;

    public sealed record CardNotFound : MoveCardResult;

    public sealed record ColumnNotFound : MoveCardResult;

    /// <summary>
    /// The client's ExpectedVersion no longer matches the row: someone else's write landed
    /// first. AuthoritativeCard is the card's current, real state (the client's own move never
    /// applied) and WinnerDisplayName names whoever's write actually won, so the loser's UI can
    /// snap to the truth and say who got there first.
    /// </summary>
    public sealed record StaleVersion(Card AuthoritativeCard, string WinnerDisplayName) : MoveCardResult;
}

public abstract record DeleteCardResult
{
    public sealed record Success : DeleteCardResult;

    public sealed record CardNotFound : DeleteCardResult;

    /// <summary>
    /// Same meaning as MoveCardResult.StaleVersion: someone else's write (a move, most likely)
    /// landed first and bumped the row's version out from under this delete request. The row
    /// still exists -- AuthoritativeCard is its current real state -- so this is a genuine
    /// rejection, distinct from the two-deletes-race case below, which is treated as success.
    /// </summary>
    public sealed record StaleVersion(Card AuthoritativeCard, string WinnerDisplayName) : DeleteCardResult;
}

public enum RejectReason
{
    Invalid,
    ColumnNotFound,
    BoardFull,
    CardNotFound,
    StaleVersion,
}

/// <summary>
/// All card mutation logic lives here, independent of SignalR: it takes plain values in and
/// returns a typed result out, never touching Hub/Clients/anything hub-related. This is what
/// lets the concurrency-conflict tests race two DbContext instances directly against this
/// class, with no SignalR in the loop at all. DeleteCard (a later phase) extends this same
/// class and follows the same result-type pattern.
/// </summary>
public sealed class CardService(AppDbContext db, IConfiguration configuration)
{
    private const int MaxCardsOnBoard = 200;
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 2000;

    // Doubles carry ~15-16 significant digits, so this sits far from actual floating-point
    // precision loss -- it's a deliberate design threshold (see spec.md), not defensive
    // padding against imprecision.
    private const double RenormalizationGapThreshold = 0.0001;

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
        uint expectedVersion,
        string movedBy
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
        var needsRenormalization = false;
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
            // Repeatedly dropping into the same tightening slot (always immediately above the
            // same lower neighbour, say) halves this gap every time: 1000, 500, 250, ... After
            // roughly 23 drops the halved gap would fall under the threshold, at which point
            // the column is renumbered instead of splitting a practically-zero gap again.
            var gap = beforeCard.Position - afterCard.Position;
            needsRenormalization = gap < RenormalizationGapThreshold * 2;
            position = (afterCard.Position + beforeCard.Position) / 2;
        }
        else if (afterCard is null && beforeCard is not null)
        {
            // Same halving problem, just against an implicit floor of 0 instead of a second
            // neighbour.
            needsRenormalization = beforeCard.Position < RenormalizationGapThreshold * 2;
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

        if (needsRenormalization)
        {
            position = await RenormalizeColumnAsync(targetColumnId, cardId, position);
        }

        // Dev-only, refused outside Development at startup (see Program.cs). Widens the real
        // race window -- normally one network round-trip -- wide enough to trigger the
        // conflict live on demand instead of only under a forced two-DbContext test.
        var artificialDelayMs = configuration.GetValue("ConcurrencyDemo:ArtificialDelayMs", 0);
        if (artificialDelayMs > 0)
        {
            await Task.Delay(artificialDelayMs);
        }

        card.ColumnId = targetColumnId;
        card.Position = position;
        card.LastModifiedBy = movedBy;

        // Forces the UPDATE's WHERE clause to check against the version the *client* last
        // saw, not whatever this method's own SELECT just returned -- that's what turns a
        // plain write into an optimistic-concurrency check. If someone else's write landed
        // between the client loading this version and this call, zero rows match and EF
        // Core throws DbUpdateConcurrencyException below, which is expected control flow
        // here, not a failure to be logged.
        db.Entry(card).Property(c => c.Version).OriginalValue = expectedVersion;

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Zero rows matched. Re-query for the row's real current state: with no
            // DeleteCard yet, nothing in this codebase can make that row disappear, so a
            // null result here is a genuine invariant violation, not a case to route
            // around silently -- fail loudly rather than let a future bug through quietly.
            var authoritative =
                await db.Cards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cardId)
                ?? throw new InvalidOperationException(
                    $"Card {cardId} vanished during a concurrency conflict, but nothing in "
                        + "this codebase can delete a card yet. This should be unreachable."
                );

            return new MoveCardResult.StaleVersion(authoritative, authoritative.LastModifiedBy ?? "another user");
        }

        return new MoveCardResult.Success(card, needsRenormalization);
    }

    public async Task<DeleteCardResult> DeleteAsync(Guid cardId, uint expectedVersion)
    {
        var card = await db.Cards.FirstOrDefaultAsync(c => c.Id == cardId);
        if (card is null)
        {
            return new DeleteCardResult.CardNotFound();
        }

        db.Cards.Remove(card);

        // Same reasoning as MoveAsync's OriginalValue force: this makes the DELETE's WHERE
        // clause check against the version the *client* last saw, not whatever this method's
        // own SELECT just returned, so a write that landed in between is caught as a
        // concurrency conflict instead of silently deleting a row someone just changed.
        db.Entry(card).Property(c => c.Version).OriginalValue = expectedVersion;

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            var authoritative = await db.Cards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cardId);
            if (authoritative is not null)
            {
                // The row still exists but with a different version: someone else's write
                // (a move, most likely) landed first.
                return new DeleteCardResult.StaleVersion(authoritative, authoritative.LastModifiedBy ?? "another user");
            }

            // The row is gone, but not because *this* delete removed it: someone else's
            // delete landed first. Deliberately treated as success, not a rejection -- the
            // caller's desired end state ("this card does not exist") is already true, and
            // there's no meaningful "rejected because already deleted" concept to surface
            // here. This makes delete idempotent under a two-deletes race.
            return new DeleteCardResult.Success();
        }

        return new DeleteCardResult.Success();
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

    /// <summary>
    /// Rewrites every other card in the column to evenly spaced positions (1000, 2000, 3000,
    /// ...) and returns the slot reserved for the moving card, in the same ordering it would
    /// have landed in under normal midpoint math. Tracked (not AsNoTracking), so these rewrites
    /// ride the same SaveChangesAsync transaction as the move itself -- each rewritten card's
    /// own naturally-loaded Version is its concurrency check, so if anyone else touched one of
    /// these cards since this method read it, that mismatch throws DbUpdateConcurrencyException
    /// right alongside the moved card's own check, and the whole move is rejected rather than
    /// partially applied.
    /// </summary>
    private async Task<double> RenormalizeColumnAsync(Guid columnId, Guid movingCardId, double provisionalPosition)
    {
        var otherCards = await db
            .Cards.Where(c => c.ColumnId == columnId && c.Id != movingCardId)
            .OrderBy(c => c.Position)
            .ToListAsync();

        const double step = 1000;
        var next = step;
        var movedPosition = 0d;
        var placedMovingCard = false;

        foreach (var other in otherCards)
        {
            if (!placedMovingCard && provisionalPosition < other.Position)
            {
                movedPosition = next;
                next += step;
                placedMovingCard = true;
            }

            other.Position = next;
            next += step;
        }

        if (!placedMovingCard)
        {
            movedPosition = next;
        }

        return movedPosition;
    }
}
