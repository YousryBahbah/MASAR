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
// Class-level [Authorize] means "any logged-in user" ONLY. Roles are set per
// action because this controller mixes Member endpoints and a management
// endpoint, and stacked [Authorize(Roles=...)] attributes are ANDed — a
// class-level Member requirement would lock managers/admins out of
// /management. CONSEQUENCE: every new action here MUST carry its own
// [Authorize(Roles = ...)], or it is open to every authenticated account.
[Authorize]
public class BookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;

    public BookingsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    [HttpPost]
    [Authorize(Roles = Roles.Member)]
    public async Task<IActionResult> Create(CreateBookingRequest request)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _bookingService.CreateAsync(userId, request);
        if (!result.Succeeded)
        {
            return StatusCode(MapCreateErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return StatusCode(StatusCodes.Status201Created, result.Response);
    }

    [HttpPost("{id:int}/checkin")]
    [Authorize(Roles = Roles.Member)]
    public async Task<IActionResult> CheckIn(int id)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _bookingService.CheckInAsync(userId, id);
        if (!result.Succeeded)
        {
            return StatusCode(MapLifecycleErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    [HttpPost("{id:int}/cancel")]
    [Authorize(Roles = Roles.Member)]
    public async Task<IActionResult> Cancel(int id)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _bookingService.CancelAsync(userId, id);
        if (!result.Succeeded)
        {
            return StatusCode(MapLifecycleErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    [HttpGet]
    [Authorize(Roles = Roles.Member)]
    public async Task<IActionResult> GetMyBookings([FromQuery] BookingHistoryRequest request)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _bookingService.GetMyBookingsAsync(userId, request);
        if (!result.Succeeded)
        {
            return StatusCode(MapLifecycleErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    [HttpGet("{id:int}")]
    [Authorize(Roles = Roles.Member)]
    public async Task<IActionResult> GetById(int id)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _bookingService.GetByIdAsync(userId, id);
        if (!result.Succeeded)
        {
            return StatusCode(MapLifecycleErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    [HttpGet("management")]
    [Authorize(Roles = $"{Roles.WorkspaceManager},{Roles.Admin}")]
    public async Task<IActionResult> GetForManagement([FromQuery] BookingManagementRequest request)
    {
        var result = await _bookingService.GetForManagementAsync(request);
        if (!result.Succeeded)
        {
            return StatusCode(MapManagementErrorCodeToStatus(result.ErrorCode!),
                new ErrorResponse(result.ErrorCode!, result.ErrorMessage!));
        }

        return Ok(result.Response);
    }

    // ClaimTypes.NameIdentifier holds user.Id — set this way in
    // JwtTokenService, confirmed there before use here. A Member can
    // only act on their own bookings; there's no "userId" in any
    // request body or route to trust instead.
    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    // Mapping follows the settled Steps 11/12 convention: 400 = malformed
    // request shape (matches a real DB CHECK constraint); 422 = coherent
    // request impossible against the clock or fixed operating hours;
    // 409 = coherent request conflicting with a resource's routinely
    // mutable current state.
    private static int MapCreateErrorCodeToStatus(string errorCode) => errorCode switch
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

    // Separate from MapCreateErrorCodeToStatus — CheckIn/Cancel have their
    // own error codes (INVALID_BOOKING_STATUS, OUTSIDE_CHECKIN_WINDOW,
    // BOOKING_NOT_FOUND) that don't belong mixed into the creation mapping.
    private static int MapLifecycleErrorCodeToStatus(string errorCode) => errorCode switch
    {
        "BOOKING_NOT_FOUND" => StatusCodes.Status404NotFound,
        // 409: the request is coherent and possible in principle, but
        // conflicts with the booking's current (mutable) status — a
        // different status entirely would make it succeed, same
        // reasoning as every other 409 in this project.
        "INVALID_BOOKING_STATUS" => StatusCodes.Status409Conflict,
        // 422: impossible against the current clock specifically, not
        // against the booking's status — same bucket as BOOKING_IN_PAST.
        "OUTSIDE_CHECKIN_WINDOW" => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError
    };

    private static int MapManagementErrorCodeToStatus(string errorCode) => errorCode switch
    {
        "VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError
    };
}
