namespace Masar.Application.Interfaces;

using Masar.Api.DTOs.Auth;
using Masar.Application.DTOs.Auth;

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest request);
    Task<AuthResult> LoginAsync(LoginRequest request);
}
