using Masar.Application.DTOs;
using Masar.Application.DTOs.Locations;
using Masar.Application.Interfaces;
using Masar.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Masar.Api.Controllers;

[ApiController]
[Route("api/locations")]
[Authorize] // baseline: any authenticated user; tightened per-action below
public class LocationsController : ControllerBase
{
    private readonly ILocationService _locationService;

    public LocationsController(ILocationService locationService)
    {
        _locationService = locationService;
    }

    [HttpPost]
    [Authorize(Roles = $"{Roles.WorkspaceManager},{Roles.Admin}")]
    public async Task<IActionResult> Create(CreateLocationRequest request)
    {
        var result = await _locationService.CreateAsync(request);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return StatusCode(StatusCodes.Status201Created, result.Response);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var locations = await _locationService.GetAllAsync();
        return Ok(locations);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var location = await _locationService.GetByIdAsync(id);
        if (location is null)
        {
            return NotFound(new ErrorResponse("LOCATION_NOT_FOUND", "Location not found."));
        }

        return Ok(location);
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = $"{Roles.WorkspaceManager},{Roles.Admin}")]
    public async Task<IActionResult> Update(int id, UpdateLocationRequest request)
    {
        var result = await _locationService.UpdateAsync(id, request);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    // Activation is Admin-only, not WorkspaceManager — deactivating a
    // location has a blast radius extending to every workspace under it,
    // which is a platform-level decision (see the locked Step 7 plan).
    [HttpPatch("{id:int}/activate")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Activate(int id)
    {
        var result = await _locationService.ActivateAsync(id);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    [HttpPatch("{id:int}/deactivate")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Deactivate(int id)
    {
        var result = await _locationService.DeactivateAsync(id);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    private static int MapErrorCodeToStatus(string errorCode) => errorCode switch
    {
        "VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
        "LOCATION_NOT_FOUND" => StatusCodes.Status404NotFound,
        "LOCATION_NAME_ALREADY_EXISTS" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };
}
