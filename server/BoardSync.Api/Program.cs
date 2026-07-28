using System.Text;
using System.Threading.RateLimiting;
using BoardSync.Api.Data;
using BoardSync.Api.Data.Entities;
using BoardSync.Api.Features.Auth;
using BoardSync.Api.Features.Board;
using BoardSync.Api.Hubs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

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

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1),

            // Read from IConfiguration inside this same lambda rather than into a variable
            // declared before AddJwtBearer is called: this configureOptions delegate is
            // invoked lazily by the options pattern (at first resolution, not at
            // AddJwtBearer() call time), which is what lets WebApplicationFactory-based
            // integration tests see their config overrides — same discipline as the
            // DbContext/CORS setup above.
            ValidIssuer = builder.Configuration["Jwt:Issuer"]
                ?? throw new InvalidOperationException("Configuration value 'Jwt:Issuer' is not set."),
            ValidAudience = builder.Configuration["Jwt:Audience"]
                ?? throw new InvalidOperationException("Configuration value 'Jwt:Audience' is not set."),
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(
                    builder.Configuration["Jwt:Secret"]
                        ?? throw new InvalidOperationException("Configuration value 'Jwt:Secret' is not set.")
                )
            ),
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                // Restricted to the hub path on purpose: browsers can't set custom headers on
                // a WebSocket handshake, forcing the token into the query string for that one
                // connection — but query strings can land in proxy/server access logs, so this
                // must never become a second, REST-usable way to authenticate.
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/board"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddSignalR();

const string ClientCorsPolicy = "Client";
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        ClientCorsPolicy,
        policy =>
        {
            var origin = builder.Configuration["CorsOrigin"]
                ?? throw new InvalidOperationException("Configuration value 'CorsOrigin' is not set.");

            // AllowCredentials is required because @microsoft/signalr's negotiate request
            // sends `credentials: 'include'` by default on a cross-origin connection (the
            // dev split between :5173 and :5080). Per the CORS spec, a credentialed request
            // requires the server to echo Access-Control-Allow-Credentials, or the browser
            // discards the response outright — this surfaced only now because nothing
            // before the SignalR hub sent a credentialed cross-origin request. Safe to pair
            // with a specific WithOrigins() (unlike AllowAnyOrigin, which CORS forbids
            // combining with credentials).
            policy.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
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

app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await DbSeeder.SeedAsync(db);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapBoardEndpoints();
app.MapAuthEndpoints();
app.MapHub<BoardHub>("/hubs/board");

app.Run();

public partial class Program;
