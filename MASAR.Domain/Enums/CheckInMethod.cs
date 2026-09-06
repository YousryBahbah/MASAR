namespace Masar.Domain.Enums;

/// <summary>
/// How a member checked in to a booking. Nullable on Booking until check-in occurs.
/// Only QR is supported in v1.
/// </summary>
public enum CheckInMethod
{
    QR = 0
}
