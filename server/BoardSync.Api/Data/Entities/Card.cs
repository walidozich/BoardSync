namespace BoardSync.Api.Data.Entities;

public sealed class Card
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required Guid ColumnId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public double Position { get; set; }
    public uint Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
