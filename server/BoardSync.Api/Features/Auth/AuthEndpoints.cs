using BoardSync.Api.Data;
using BoardSync.Api.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Features.Auth;

public record RegisterRequest(string Email, string Password, string DisplayName);

public record LoginRequest(string Email, string Password);

public record AuthResponse(string Token, string DisplayName);

public static class AuthEndpoints
{
    public const string RateLimiterPolicy = "auth";

    private const int MinPasswordLength = 8;
    private const int MaxDisplayNameLength = 50;
    private const string InvalidCredentialsMessage = "Invalid email or password.";

    // Verified against when no user matches, so login spends the same hashing work on a
    // missing account as on a wrong password and leaks nothing through response timing.
    private static readonly User DecoyUser = new()
    {
        Email = "decoy@boardsync.invalid",
        DisplayName = "decoy",
        PasswordHash = string.Empty,
    };

    private static readonly string DecoyPasswordHash = new PasswordHasher<User>().HashPassword(
        DecoyUser,
        "decoy-password-never-matches"
    );

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").RequireRateLimiting(RateLimiterPolicy);

        group.MapPost("/register", RegisterAsync);
        group.MapPost("/login", LoginAsync);
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        JwtTokenService tokenService,
        CancellationToken cancellationToken
    )
    {
        var errors = new Dictionary<string, string[]>();

        var email = request.Email?.Trim() ?? string.Empty;
        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;

        if (email.Length == 0 || !email.Contains('@'))
        {
            errors[nameof(RegisterRequest.Email)] = ["Email must be a non-empty address containing '@'."];
        }

        if (password.Length < MinPasswordLength)
        {
            errors[nameof(RegisterRequest.Password)] =
                [$"Password must be at least {MinPasswordLength} characters."];
        }

        if (displayName.Length is 0 or > MaxDisplayNameLength)
        {
            errors[nameof(RegisterRequest.DisplayName)] =
                [$"Display name must be between 1 and {MaxDisplayNameLength} characters."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // .ToLower() is translated by Npgsql to Postgres lower(), matching the
        // unique index on lower(email); an in-memory comparison would not be translatable.
        var emailExists = await db
            .Users.AsNoTracking()
            .AnyAsync(u => u.Email.ToLower() == email.ToLower(), cancellationToken);

        if (emailExists)
        {
            return Results.Conflict(new { message = "An account with that email already exists." });
        }

        var user = new User
        {
            Email = email,
            DisplayName = displayName,
            PasswordHash = string.Empty,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        var token = tokenService.IssueToken(user);

        return Results.Created($"/api/users/{user.Id}", new AuthResponse(token, user.DisplayName));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        JwtTokenService tokenService,
        CancellationToken cancellationToken
    )
    {
        var email = request.Email?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;

        // Same query pattern as register: translated to Postgres lower(email).
        var user = await db
            .Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower(), cancellationToken);

        // Verification always runs, against the decoy hash when no user matched, so the two
        // failure modes cost the same. Both converge on the single failure return below, so the
        // response body, status, and timing never reveal whether an account exists.
        var verificationResult = passwordHasher.VerifyHashedPassword(
            user ?? DecoyUser,
            user?.PasswordHash ?? DecoyPasswordHash,
            password
        );

        var verified =
            user is not null
            && verificationResult
                is PasswordVerificationResult.Success
                    or PasswordVerificationResult.SuccessRehashNeeded;

        if (!verified)
        {
            return Results.Json(
                new { message = InvalidCredentialsMessage },
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var token = tokenService.IssueToken(user!);

        return Results.Ok(new AuthResponse(token, user!.DisplayName));
    }
}
