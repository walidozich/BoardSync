using System.Threading.RateLimiting;
using BoardSync.Api.Data;
using BoardSync.Api.Data.Entities;
using BoardSync.Api.Features.Auth;
using BoardSync.Api.Features.Board;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
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

builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

// Stateless apart from reading IConfiguration on each IssueToken call, which keeps
// test-supplied configuration overrides visible (see the DbContext note above).
builder.Services.AddSingleton<JwtTokenService>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Partitioned by client IP: AddFixedWindowLimiter would share a single window across
    // every caller, so one client could lock everyone else out of login. Limit and window
    // are configurable (not hardcoded) so integration tests — which share one app instance,
    // and therefore one rate-limit partition, across many functional test cases — can raise
    // the budget instead of tripping it as a side effect of unrelated assertions.
    options.AddPolicy<string>(
        AuthEndpoints.RateLimiterPolicy,
        httpContext =>
        {
            var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
            var permitLimit = config.GetValue("RateLimiting:Auth:PermitLimit", 10);
            var windowMinutes = config.GetValue("RateLimiting:Auth:WindowMinutes", 1);

            return RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(windowMinutes),
                }
            );
        }
    );
});

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

app.UseRateLimiter();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await DbSeeder.SeedAsync(db);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapBoardEndpoints();
app.MapAuthEndpoints();

app.Run();

public partial class Program;
