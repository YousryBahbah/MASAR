using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MASAR.Application.Interfaces;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Masar.Api.Configuration;
using Masar.Domain.Entities;

namespace Masar.Api.Services;

public class JwtTokenService : ITokenService
{
    private readonly JwtSettings _settings;

    public JwtTokenService(IOptions<JwtSettings> settings)
    {
        _settings = settings.Value;
    }

    public (string AccessToken, DateTime ExpiresAtUtc) CreateAccessToken(
        ApplicationUser user,
        IReadOnlyList<string> roles)
    {
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(_settings.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, user.Id),
            new("firstName", user.FirstName),
            new("lastName", user.LastName)
        };

        // One ClaimTypes.Role claim per role — this is what [Authorize(Roles = "...")]
        // reads against, so it must use ClaimTypes.Role, not a custom claim type.
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);
        return (accessToken, expiresAtUtc);
    }
}
