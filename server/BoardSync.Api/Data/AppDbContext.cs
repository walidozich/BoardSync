using BoardSync.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<User> Users => Set<User>();
    public DbSet<BoardColumn> BoardColumns => Set<BoardColumn>();
    public DbSet<Card> Cards => Set<Card>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Board>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<User>(b =>
        {
            b.Property(x => x.Email).HasMaxLength(320).IsRequired();
            b.Property(x => x.PasswordHash).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(50).IsRequired();
        });

        modelBuilder.Entity<BoardColumn>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(100).IsRequired();
            b.HasIndex(c => new { c.BoardId, c.Position });
            // No navigation property by design (entities stay flat), but the FK constraint
            // itself still matters: without it, a bug in later card/column mutation logic
            // could silently orphan rows with no database-level guardrail.
            b.HasOne<Board>().WithMany().HasForeignKey(c => c.BoardId);
        });

        modelBuilder.Entity<Card>(b =>
        {
            b.Property(c => c.Title).HasMaxLength(200).IsRequired();
            b.Property(c => c.Description).HasMaxLength(2000);
            b.Property(c => c.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            b.HasIndex(c => new { c.ColumnId, c.Position });
            b.HasOne<BoardColumn>().WithMany().HasForeignKey(c => c.ColumnId);
        });
    }
}
