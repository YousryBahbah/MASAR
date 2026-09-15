using Masar.Application.DTOs;
using Masar.Application.DTOs.Amenities;
using Masar.Application.DTOs.Workspaces;
using Masar.Application.Interfaces;
using Masar.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Masar.Api.Controllers;

[ApiController]
[Route("api/workspaces")]
[Authorize] // baseline: any authenticated user; tightened per-action below
public class WorkspacesController : ControllerBase
{
    private readonly IWorkspaceService _workspaceService;
    private readonly IAmenityService _amenityService;

    public WorkspacesController(IWorkspaceService workspaceService, IAmenityService amenityService)
    {
        _workspaceService = workspaceService;
        _amenityService = amenityService;
    }

    [HttpPost]
    [Authorize(Roles = $"{Roles.WorkspaceManager},{Roles.Admin}")]
    public async Task<IActionResult> Create(CreateWorkspaceRequest request)
    {
        var result = await _workspaceService.CreateAsync(request);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return StatusCode(StatusCodes.Status201Created, result.Response);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var workspace = await _workspaceService.GetByIdAsync(id);
        if (workspace is null)
        {
            return NotFound(new ErrorResponse("WORKSPACE_NOT_FOUND", "Workspace not found."));
        }

        return Ok(workspace);
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = $"{Roles.WorkspaceManager},{Roles.Admin}")]
    public async Task<IActionResult> Update(int id, UpdateWorkspaceRequest request)
    {
        var result = await _workspaceService.UpdateAsync(id, request);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    // Activation is WorkspaceManager-level, NOT Admin-only — deliberate
    // asymmetry with Location (see the locked Step 8 plan). Taking one
    // room offline for cleaning/repair is an operational decision; taking
    // an entire location offline is a platform-level one.
    [HttpPatch("{id:int}/activate")]
    [Authorize(Roles = $"{Roles.WorkspaceManager},{Roles.Admin}")]
    public async Task<IActionResult> Activate(int id)
    {
        var result = await _workspaceService.ActivateAsync(id);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    [HttpPatch("{id:int}/deactivate")]
    [Authorize(Roles = $"{Roles.WorkspaceManager},{Roles.Admin}")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var result = await _workspaceService.DeactivateAsync(id);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    // Lives here, not on AmenitiesController — the resource being
    // modified is a workspace's amenity set, matching the plan's route
    // (/api/workspaces/{id}/amenities), same WorkspaceManager/Admin
    // restriction as the rest of workspace management.
    [HttpPut("{id:int}/amenities")]
    [Authorize(Roles = $"{Roles.WorkspaceManager},{Roles.Admin}")]
    public async Task<IActionResult> UpdateAmenities(int id, UpdateWorkspaceAmenitiesRequest request)
    {
        var result = await _amenityService.UpdateWorkspaceAmenitiesAsync(id, request);
        if (!result.Succeeded)
        {
            return StatusCode(MapAmenitiesErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    private static int MapErrorCodeToStatus(string errorCode) => errorCode switch
    {
        "VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
        "WORKSPACE_NOT_FOUND" => StatusCodes.Status404NotFound,
        "LOCATION_NOT_FOUND" => StatusCodes.Status404NotFound,
        "LOCATION_INACTIVE" => StatusCodes.Status409Conflict,
        "WORKSPACE_NAME_ALREADY_EXISTS_IN_LOCATION" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };

    // Separate from MapErrorCodeToStatus above: WORKSPACE_NOT_FOUND is
    // shared with it, but AMENITY_NOT_FOUND is specific to this endpoint
    // and doesn't belong in the workspace-CRUD mapping above.
    private static int MapAmenitiesErrorCodeToStatus(string errorCode) => errorCode switch
    {
        "VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
        "WORKSPACE_NOT_FOUND" => StatusCodes.Status404NotFound,
        "AMENITY_NOT_FOUND" => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status500InternalServerError
    };
}
