namespace Masar.Api.Configuration;

/// <summary>
/// Bound from the "Jwt" configuration section. Values come from
/// appsettings.json in Development and from environment
/// variables / a secrets manager in Production — never hardcoded,
/// and the signing key must never be committed to source control.
/// </summary>
public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = null!;
    public string Audience { get; set; } = null!;
    public string SigningKey { get; set; } = null!;
    public int AccessTokenMinutes { get; set; } = 60;
}
