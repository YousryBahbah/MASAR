using FluentValidation;
using Masar.Application.Common;
using Masar.Application.DTOs.Maintenance;

namespace Masar.Application.Validators.Maintenance;

public class CreateMaintenancePeriodRequestValidator : AbstractValidator<CreateMaintenancePeriodRequest>
{
    public CreateMaintenancePeriodRequestValidator()
    {
        RuleFor(x => x.WorkspaceId).GreaterThan(0);
        RuleFor(x => x.StartDate).NotNull();
        RuleFor(x => x.StartTime).NotNull();
        RuleFor(x => x.EndDate).NotNull();
        RuleFor(x => x.EndTime).NotNull();
        MaintenanceIntervalRules.Add(this, x => x.StartDate, x => x.StartTime, x => x.EndDate, x => x.EndTime);
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public class UpdateMaintenancePeriodRequestValidator : AbstractValidator<UpdateMaintenancePeriodRequest>
{
    public UpdateMaintenancePeriodRequestValidator()
    {
        RuleFor(x => x.StartDate).NotNull();
        RuleFor(x => x.StartTime).NotNull();
        RuleFor(x => x.EndDate).NotNull();
        RuleFor(x => x.EndTime).NotNull();
        MaintenanceIntervalRules.Add(this, x => x.StartDate, x => x.StartTime, x => x.EndDate, x => x.EndTime);
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

// Shared by Create and Update so the two can't drift apart. Every rule only
// runs once the four date/time fields are present, so none can throw on a
// null .Value access.
internal static class MaintenanceIntervalRules
{
    public static void Add<T>(
        AbstractValidator<T> validator,
        Func<T, DateOnly?> startDate,
        Func<T, TimeOnly?> startTime,
        Func<T, DateOnly?> endDate,
        Func<T, TimeOnly?> endTime)
    {
        // A wall-clock time inside Egypt's DST spring-forward gap does not
        // exist, and EgyptTime.ToUtc would throw on it (a 500). Maintenance
        // accepts any time of day (including 00:00-01:00), so it can hit
        // this; reject it here as an ordinary malformed request.
        validator.RuleFor(x => x)
            .Must(x => EgyptTime.IsValidLocalTime(startDate(x)!.Value, startTime(x)!.Value))
            .When(x => startDate(x).HasValue && startTime(x).HasValue)
            .WithMessage("The maintenance start does not exist in Egypt local time (daylight-saving gap).");

        validator.RuleFor(x => x)
            .Must(x => EgyptTime.IsValidLocalTime(endDate(x)!.Value, endTime(x)!.Value))
            .When(x => endDate(x).HasValue && endTime(x).HasValue)
            .WithMessage("The maintenance end does not exist in Egypt local time (daylight-saving gap).");

        validator.RuleFor(x => x)
            .Must(x => startDate(x)!.Value.ToDateTime(startTime(x)!.Value) <
                       endDate(x)!.Value.ToDateTime(endTime(x)!.Value))
            .When(x => startDate(x).HasValue && startTime(x).HasValue &&
                       endDate(x).HasValue && endTime(x).HasValue)
            .WithMessage("The maintenance start must be before its end.");
    }
}
