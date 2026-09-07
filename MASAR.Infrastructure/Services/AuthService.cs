using FluentValidation;
using Masar.Application.DTOs.Auth;
using Masar.Application.Interfaces;
using Masar.Domain.Entities;
using Masar.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Masar.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly IValidator<RegisterRequest> _registerValidator;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        ITokenService tokenService,
        IValidator<RegisterRequest> registerValidator,
        IValidator<LoginRequest> loginValidator,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _registerValidator = registerValidator;
        _loginValidator = loginValidator;
        _logger = logger;
    }

    public async Task<AuthResult> RegisterAsync(RegisterRequest request)
    {
        var validation = await _registerValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var validationMessage = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
            return AuthResult.Failure("VALIDATION_FAILED", validationMessage);
        }

        var existing = await _userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
        {
            return AuthResult.Failure("DUPLICATE_EMAIL", "A user with this email already exists.");
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FirstName = request.FirstName,
            LastName = request.LastName,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var createResult = await _userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            var message = string.Join(" ", createResult.Errors.Select(e => e.Description));
            return AuthResult.Failure("REGISTRATION_FAILED", message);
        }

        var roleResult = await _userManager.AddToRoleAsync(user, Roles.Member);
        if (!roleResult.Succeeded)
        {
            var message = string.Join(" ", roleResult.Errors.Select(e => e.Description));
            _logger.LogError("Role assignment failed for user {UserId}: {Message}", user.Id, message);
            return AuthResult.Failure("ROLE_ASSIGNMENT_FAILED", message);
        }

        var (accessToken, expiresAtUtc) = _tokenService.CreateAccessToken(
            user, new List<string> { Roles.Member });

        var response = new AuthResponse(
            user.Id, user.Email!, user.FirstName, user.LastName,
            new List<string> { Roles.Member }, accessToken, expiresAtUtc);

        _logger.LogInformation("User {UserId} registered.", user.Id);

        return AuthResult.Success(response);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request)
    {
        var loginValidation = await _loginValidator.ValidateAsync(request);
        if (!loginValidation.IsValid)
        {
            var validationMessage = string.Join(" ", loginValidation.Errors.Select(e => e.ErrorMessage));
            return AuthResult.Failure("VALIDATION_FAILED", validationMessage);
        }

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null || !user.IsActive)
        {
            _logger.LogWarning("Login failed for {Email}.", request.Email);
            return AuthResult.Failure("INVALID_CREDENTIALS", "Email or password is incorrect.");
        }

        var passwordValid = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!passwordValid)
        {
            _logger.LogWarning("Login failed for {Email}: incorrect password.", request.Email);
            return AuthResult.Failure("INVALID_CREDENTIALS", "Email or password is incorrect.");
        }

        var roles = await _userManager.GetRolesAsync(user);
        var (accessToken, expiresAtUtc) = _tokenService.CreateAccessToken(user, roles.ToList());

        var response = new AuthResponse(
            user.Id, user.Email!, user.FirstName, user.LastName,
            roles.ToList(), accessToken, expiresAtUtc);

        _logger.LogInformation("User {UserId} logged in.", user.Id);

        return AuthResult.Success(response);
    }
}
