using FluentValidation;
using Masar.Application.DTOs.Bookings;

namespace Masar.Application.Validators.Bookings;

public class CreateBookingRequestValidator : AbstractValidator<CreateBookingRequest>
{
    public CreateBookingRequestValidator()
    {
        RuleFor(x => x.WorkspaceId).GreaterThan(0);
        RuleFor(x => x.Date).NotNull();
        RuleFor(x => x.StartTime).NotNull();
        RuleFor(x => x.EndTime).NotNull();

        // Matches CK_Booking_StartBeforeEnd — a malformed interval, the
        // same class as a real DB CHECK constraint, not a business-state
        // conflict. Both land as 400 VALIDATION_FAILED per the settled
        // Steps 11/12 convention, same bucket as the NotNull checks
        // above — only runs once all three fields are actually present,
        // so it can't throw on a null .Value access.
        RuleFor(x => x)
            .Must(x => x.StartTime!.Value < x.EndTime!.Value)
            .When(x => x.StartTime.HasValue && x.EndTime.HasValue)
            .WithMessage("startTime must be before endTime.");
    }
}
