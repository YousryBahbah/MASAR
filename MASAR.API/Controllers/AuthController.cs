using Masar.Application.DTOs;
using Masar.Application.DTOs.Auth;
using Masar.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Masar.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var result = await _authService.RegisterAsync(request);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return StatusCode(StatusCodes.Status201Created, result.Response);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var result = await _authService.LoginAsync(request);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    // Plain switch, right here in the controller — this is API-layer
    // concern (HTTP status selection), not something Application needs to
    // know about. Application only ever deals in ErrorCode strings.
    private static int MapErrorCodeToStatus(string errorCode) => errorCode switch
    {
        "DUPLICATE_EMAIL" => StatusCodes.Status409Conflict,
        "INVALID_CREDENTIALS" => StatusCodes.Status401Unauthorized,
        "REGISTRATION_FAILED" => StatusCodes.Status400BadRequest,
        "ROLE_ASSIGNMENT_FAILED" => StatusCodes.Status500InternalServerError,
        "VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError
    };
}
