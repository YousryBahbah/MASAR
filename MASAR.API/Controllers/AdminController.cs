using System.Security.Claims;
using Masar.Application.DTOs;
using Masar.Application.DTOs.Admin;
using Masar.Application.Interfaces;
using Masar.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Masar.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = Roles.Admin)]
public class AdminController : ControllerBase
{
    private readonly IAdminService _adminService;

    public AdminController(IAdminService adminService)
    {
        _adminService = adminService;
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers([FromQuery] AdminUserListRequest request)
    {
        var result = await _adminService.GetUsersAsync(request);
        return ToActionResult(result);
    }

    [HttpGet("users/{id}")]
    public async Task<IActionResult> GetUser(string id)
    {
        var result = await _adminService.GetUserByIdAsync(id);
        return ToActionResult(result);
    }

    [HttpPut("users/{id}/roles")]
    public async Task<IActionResult> UpdateRoles(string id, UpdateUserRolesRequest request)
    {
        var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actorUserId is null)
        {
            return Unauthorized();
        }

        var result = await _adminService.UpdateUserRolesAsync(actorUserId, id, request);
        return ToActionResult(result);
    }

    [HttpPatch("users/{id}/activate")]
    public async Task<IActionResult> Activate(string id) => await SetUserActiveAsync(id, true);

    [HttpPatch("users/{id}/deactivate")]
    public async Task<IActionResult> Deactivate(string id) => await SetUserActiveAsync(id, false);

    private async Task<IActionResult> SetUserActiveAsync(string id, bool isActive)
    {
        var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actorUserId is null)
        {
            return Unauthorized();
        }

        var result = await _adminService.SetUserActiveAsync(actorUserId, id, isActive);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Masar.Application.Common.Result<T> result)
    {
        if (result.Succeeded)
        {
            return Ok(result.Response);
        }

        return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
            new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
    }

    private static int MapErrorCodeToStatus(string errorCode) => errorCode switch
    {
        "VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
        "USER_NOT_FOUND" => StatusCodes.Status404NotFound,
        "SELF_SERVICE_NOT_ALLOWED" => StatusCodes.Status409Conflict,
        "LAST_ACTIVE_ADMIN_PROTECTED" => StatusCodes.Status409Conflict,
        "CONCURRENT_ADMIN_CHANGE" => StatusCodes.Status503ServiceUnavailable,
        "ROLE_UPDATE_FAILED" => StatusCodes.Status500InternalServerError,
        "USER_UPDATE_FAILED" => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError
    };
}
