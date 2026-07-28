using BoardSync.Api.Data;
using BoardSync.Api.Features.Board;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Resolved lazily via IConfiguration at first DbContext use, not read eagerly here.
// WebApplicationFactory (integration tests) only merges its configuration overrides
// during builder.Build(); reading the connection string before that point would see
// the un-overridden value.
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
        ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

    options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
});

builder.Services.AddOpenApi();

const string ClientCorsPolicy = "Client";
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        ClientCorsPolicy,
        policy =>
        {
            var origin = builder.Configuration["CorsOrigin"]
                ?? throw new InvalidOperationException("Configuration value 'CorsOrigin' is not set.");
            policy.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod();
        }
    );
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(ClientCorsPolicy);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await DbSeeder.SeedAsync(db);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapBoardEndpoints();

app.Run();

public partial class Program;
