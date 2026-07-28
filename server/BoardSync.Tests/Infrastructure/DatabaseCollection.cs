namespace BoardSync.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<BoardSyncApiFactory>
{
    public const string Name = "Database";
}
