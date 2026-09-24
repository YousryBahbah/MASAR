using Masar.Application.Common;
using Masar.Application.DTOs.Bookings;

namespace Masar.Application.Interfaces;

public interface IBookingService
{
    // userId comes from the authenticated caller's claims, not the
    // request body — a Member can only ever book for themselves, there
    // is no "book on behalf of another user" concept in v1.
    Task<Result<BookingResponse>> CreateAsync(string userId, CreateBookingRequest request);
}
