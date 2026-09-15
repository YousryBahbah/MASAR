using Masar.Application.DTOs;
using Masar.Application.DTOs.Amenities;
using Masar.Application.Interfaces;
using Masar.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Masar.Api.Controllers;

[ApiController]
[Route("api/amenities")]
[Authorize] // baseline: any authenticated user; tightened per-action below
public class AmenitiesController : ControllerBase
{
    private readonly IAmenityService _amenityService;

    public AmenitiesController(IAmenityService amenityService)
    {
        _amenityService = amenityService;
    }

    // Creation is Admin-only, not WorkspaceManager — the amenity catalog
    // itself is platform-level; which amenities a specific workspace has
    // is what WorkspaceManager controls (see WorkspacesController).
    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Create(CreateAmenityRequest request)
    {
        var result = await _amenityService.CreateAsync(request);
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
        var amenities = await _amenityService.GetAllAsync();
        return Ok(amenities);
    }

    private static int MapErrorCodeToStatus(string errorCode) => errorCode switch
    {
        "VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
        "AMENITY_NAME_ALREADY_EXISTS" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };
}
