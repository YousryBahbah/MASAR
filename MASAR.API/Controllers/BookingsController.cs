using System.Security.Claims;
using Masar.Application.DTOs;
using Masar.Application.DTOs.Bookings;
using Masar.Application.Interfaces;
using Masar.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Masar.Api.Controllers;

[ApiController]
[Route("api/bookings")]
[Authorize(Roles = Roles.Member)] // Member-only, per the Step 2 recap — unchanged
public class BookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;

    public BookingsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateBookingRequest request)
    {
        // ClaimTypes.NameIdentifier holds user.Id — set this way in
        // JwtTokenService, confirmed there before use here. A Member can
        // only book for themselves; there's no "userId" in the request
        // body to trust instead.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _bookingService.CreateAsync(userId, request);
        if (!result.Succeeded)
        {
            return StatusCode(MapErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return StatusCode(StatusCodes.Status201Created, result.Response);
    }

    // Mapping follows the settled Steps 11/12 convention: 400 = malformed
    // request shape (matches a real DB CHECK constraint); 422 = coherent
    // request impossible against the clock or fixed operating hours;
    // 409 = coherent request conflicting with a resource's routinely
    // mutable current state.
    private static int MapErrorCodeToStatus(string errorCode) => errorCode switch
    {
        "VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
        "WORKSPACE_NOT_FOUND" => StatusCodes.Status404NotFound,
        "WORKSPACE_INACTIVE" => StatusCodes.Status409Conflict,
        "LOCATION_INACTIVE" => StatusCodes.Status409Conflict,
        "BOOKING_IN_PAST" => StatusCodes.Status422UnprocessableEntity,
        "OUTSIDE_OPERATING_HOURS" => StatusCodes.Status422UnprocessableEntity,
        "MAINTENANCE_CONFLICT" => StatusCodes.Status409Conflict,
        "ACTIVE_BOOKING_LIMIT_EXCEEDED" => StatusCodes.Status409Conflict,
        "WORKSPACE_UNAVAILABLE" => StatusCodes.Status409Conflict,
        // Distinct from WORKSPACE_UNAVAILABLE on purpose — this is SQL
        // Server reporting a deadlock (infrastructure), not the overlap
        // check reporting a real conflicting booking (business state).
        // 503, not 500: the request itself was fine: retrying the exact
        // same request later is expected to succeed, which is what 503
        // signals to a client, unlike a generic 500.
        "CONCURRENT_WRITE_CONFLICT" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };
}
