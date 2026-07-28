using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BoardSync.Api.Data.Entities;
using Microsoft.IdentityModel.Tokens;

namespace BoardSync.Api.Features.Auth;

/// <summary>
/// Issues signed JWTs for authenticated users. Configuration is read on every call rather than
/// cached at construction time so that WebApplicationFactory-based integration tests, which merge
/// their overrides during builder.Build(), see the values they supplied.
/// </summary>
public sealed class JwtTokenService(IConfiguration configuration)
{
    public string IssueToken(User user)
    {
        var secret = configuration["Jwt:Secret"]
            ?? throw new InvalidOperationException("Configuration value 'Jwt:Secret' is not set.");
        var issuer = configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException("Configuration value 'Jwt:Issuer' is not set.");
        var audience = configuration["Jwt:Audience"]
            ?? throw new InvalidOperationException("Configuration value 'Jwt:Audience' is not set.");
        var lifetimeHours = configuration.GetValue<int?>("Jwt:LifetimeHours")
            ?? throw new InvalidOperationException("Configuration value 'Jwt:LifetimeHours' is not set.");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new(JwtRegisteredClaimNames.Email, user.Email),
        ];

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(lifetimeHours),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
