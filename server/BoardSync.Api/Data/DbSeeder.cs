using BoardSync.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Data;

public static class DbSeeder
{
    public const string DemoBoardName = "BoardSync Demo";

    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.Boards.AnyAsync())
        {
            return;
        }

        db.Boards.Add(new Board { Name = DemoBoardName });
        await db.SaveChangesAsync();
    }
}
