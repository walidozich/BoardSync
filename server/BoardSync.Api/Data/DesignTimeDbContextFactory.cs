using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BoardSync.Api.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=boardsync_design;Username=design;Password=design";

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();

        return new AppDbContext(optionsBuilder.Options);
    }
}
