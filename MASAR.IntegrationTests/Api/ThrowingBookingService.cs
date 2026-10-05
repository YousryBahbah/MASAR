using Masar.Application.Common;
using Masar.Application.DTOs.Bookings;
using Masar.Application.Interfaces;

namespace Masar.IntegrationTests.Api;

// Test double that fails every call with a message that must never reach a
// client. Used only to prove the global exception handler is wired in.
internal sealed class ThrowingBookingService : IBookingService
{
    private static InvalidOperationException Boom() =>
        new("SECRET-INTERNAL-DETAIL: connection string Server=prod;Password=hunter2");

    public Task<Result<BookingResponse>> CreateAsync(string userId, CreateBookingRequest request) => throw Boom();
    public Task<Result<BookingResponse>> CheckInAsync(string userId, int bookingId) => throw Boom();
    public Task<Result<BookingResponse>> CancelAsync(string userId, int bookingId) => throw Boom();
    public Task<Result<BookingHistoryResponse>> GetMyBookingsAsync(string userId, BookingHistoryRequest request) => throw Boom();
    public Task<Result<BookingResponse>> GetByIdAsync(string userId, int bookingId) => throw Boom();
    public Task<Result<BookingManagementResponse>> GetForManagementAsync(BookingManagementRequest request) => throw Boom();
}
