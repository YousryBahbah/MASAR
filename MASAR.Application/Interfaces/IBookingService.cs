using Masar.Application.Common;
using Masar.Application.DTOs.Bookings;

namespace Masar.Application.Interfaces;

public interface IBookingService
{
    // userId comes from the authenticated caller's claims, not the
    // request body — a Member can only ever book for themselves, there
    // is no "book on behalf of another user" concept in v1.
    Task<Result<BookingResponse>> CreateAsync(string userId, CreateBookingRequest request);

    // Same not-found/not-yours -> BOOKING_NOT_FOUND reasoning for both
    // of the methods below: see BookingService for why a booking that
    // exists but belongs to someone else isn't distinguished from one
    // that doesn't exist at all.
    Task<Result<BookingResponse>> CheckInAsync(string userId, int bookingId);

    Task<Result<BookingResponse>> CancelAsync(string userId, int bookingId);
}
