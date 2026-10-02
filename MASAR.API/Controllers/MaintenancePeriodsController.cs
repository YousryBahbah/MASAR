using System.Security.Claims;
using Masar.Application.DTOs;
using Masar.Application.DTOs.Maintenance;
using Masar.Application.Interfaces;
using Masar.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Masar.Api.Controllers;

[ApiController]
[Route("api/maintenance-periods")]
[Authorize(Roles = $"{Roles.WorkspaceManager},{Roles.Admin}")]
public class MaintenancePeriodsController : ControllerBase
{
    private readonly IMaintenancePeriodService _maintenancePeriodService;

    public MaintenancePeriodsController(IMaintenancePeriodService maintenancePeriodService)
    {
        _maintenancePeriodService = maintenancePeriodService;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateMaintenancePeriodRequest request)
    {
        // Program.cs suppresses [ApiController]'s automatic invalid-ModelState
        // 400, so a body that fails to bind (malformed JSON, empty body,
        // wrong Content-Type) arrives here as null. Without this guard it
        // would surface as a 500 from a null dereference deeper down.
        if (!ModelState.IsValid || request is null)
        {
            return BadRequest(new ErrorResponse(
                "VALIDATION_FAILED", "Request body is missing or malformed."));
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _maintenancePeriodService.CreateAsync(userId, request);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return StatusCode(StatusCodes.Status201Created, result.Response);
    }

    [HttpGet("workspace/{workspaceId:int}")]
    public async Task<IActionResult> GetByWorkspace(int workspaceId)
    {
        var result = await _maintenancePeriodService.GetByWorkspaceAsync(workspaceId);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var result = await _maintenancePeriodService.GetByIdAsync(id);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateMaintenancePeriodRequest request)
    {
        // Program.cs suppresses [ApiController]'s automatic invalid-ModelState
        // 400, so a body that fails to bind (malformed JSON, empty body,
        // wrong Content-Type) arrives here as null. Without this guard it
        // would surface as a 500 from a null dereference deeper down.
        if (!ModelState.IsValid || request is null)
        {
            return BadRequest(new ErrorResponse(
                "VALIDATION_FAILED", "Request body is missing or malformed."));
        }

        var result = await _maintenancePeriodService.UpdateAsync(id, request);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _maintenancePeriodService.DeleteAsync(id);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return NoContent();
    }

    private static int MapErrorCodeToStatus(string errorCode) => errorCode switch
    {
        "VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
        "WORKSPACE_NOT_FOUND" => StatusCodes.Status404NotFound,
        "MAINTENANCE_PERIOD_NOT_FOUND" => StatusCodes.Status404NotFound,
        // 409: a coherent request that conflicts with routinely mutable
        // state (live bookings) — same bucket as MAINTENANCE_CONFLICT on
        // the booking side.
        "MAINTENANCE_BOOKING_CONFLICT" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };
}
