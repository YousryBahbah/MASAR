using FluentValidation;
using Masar.Application.Common;
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

        // A wall-clock time inside Egypt's DST spring-forward gap does not
        // exist, and EgyptTime.ToUtc would throw on it (a 500). Reject it
        // here as an ordinary malformed request instead.
        RuleFor(x => x)
            .Must(x => EgyptTime.IsValidLocalTime(x.Date!.Value, x.StartTime!.Value))
            .When(x => x.Date.HasValue && x.StartTime.HasValue)
            .WithMessage("startTime does not exist on that date in Egypt local time (daylight-saving gap).");

        RuleFor(x => x)
            .Must(x => EgyptTime.IsValidLocalTime(x.Date!.Value, x.EndTime!.Value))
            .When(x => x.Date.HasValue && x.EndTime.HasValue)
            .WithMessage("endTime does not exist on that date in Egypt local time (daylight-saving gap).");
    }
}
