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

public enum RejectReason
{
    Invalid,
    ColumnNotFound,
    BoardFull,
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

        var maxPosition = await db
            .Cards.Where(c => c.ColumnId == columnId)
            .Select(c => (double?)c.Position)
            .MaxAsync();

        var card = new Card
        {
            ColumnId = columnId,
            Title = trimmedTitle,
            Description = trimmedDescription,
            Position = (maxPosition ?? 0) + 1000,
        };

        db.Cards.Add(card);
        await db.SaveChangesAsync();

        return new CreateCardResult.Success(card);
    }
}
