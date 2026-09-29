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

    // Step 14 — a Member's own booking history. Self-scoped only: no
    // "view another user's bookings" path exists here or anywhere else
    // yet. That's deliberately deferred to Step 15 (Admin & Platform
    // Management), not built implicitly by loosening this method's
    // filter — see the Step 13-17 roadmap's own reasoning for keeping
    // this step Member-only rather than splitting one concern's
    // authorization logic across two steps.
    Task<Result<BookingHistoryResponse>> GetMyBookingsAsync(string userId, BookingHistoryRequest request);

    // Same not-found/not-yours -> BOOKING_NOT_FOUND pattern as
    // CheckInAsync/CancelAsync above — a booking that exists but isn't
    // the caller's own is indistinguishable from one that doesn't
    // exist at all.
    Task<Result<BookingResponse>> GetByIdAsync(string userId, int bookingId);
}
