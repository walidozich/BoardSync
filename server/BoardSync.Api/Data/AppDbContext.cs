using BoardSync.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Board> Boards => Set<Board>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Board>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });
    }
}
