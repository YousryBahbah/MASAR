using Masar.Domain.Entities;
namespace Masar.Application.Interfaces;

public interface ITokenService
{
    /// <summary>
    /// Generates a signed JWT containing identity and role claims for the user.
    /// </summary>
    (string AccessToken, DateTime ExpiresAtUtc) CreateAccessToken(
        ApplicationUser user,
        IReadOnlyList<string> roles);
}
