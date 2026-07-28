namespace BoardSync.Api.Data.Entities;

public sealed class BoardColumn
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required Guid BoardId { get; set; }
    public required string Name { get; set; }
    public double Position { get; set; }
}
