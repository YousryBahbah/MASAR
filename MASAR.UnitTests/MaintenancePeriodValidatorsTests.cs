using Masar.Application.DTOs.Maintenance;
using Masar.Application.Validators.Maintenance;
using Xunit;

namespace Masar.UnitTests;

public class MaintenancePeriodValidatorsTests
{
    private static readonly CreateMaintenancePeriodRequestValidator CreateValidator = new();
    private static readonly UpdateMaintenancePeriodRequestValidator UpdateValidator = new();

    private static readonly DateOnly Day = new(2030, 6, 15);

    private static CreateMaintenancePeriodRequest ValidCreate() =>
        new(1, Day, new TimeOnly(9, 0), Day, new TimeOnly(13, 0), "Repainting");

    private static UpdateMaintenancePeriodRequest ValidUpdate() =>
        new(Day, new TimeOnly(9, 0), Day, new TimeOnly(13, 0), "Repainting");

    private static string[] Errors(CreateMaintenancePeriodRequest r) =>
        CreateValidator.Validate(r).Errors.Select(e => e.ErrorMessage).ToArray();

    private static string[] Errors(UpdateMaintenancePeriodRequest r) =>
        UpdateValidator.Validate(r).Errors.Select(e => e.ErrorMessage).ToArray();

    [Fact]
    public void Valid_requests_pass()
    {
        Assert.True(CreateValidator.Validate(ValidCreate()).IsValid);
        Assert.True(UpdateValidator.Validate(ValidUpdate()).IsValid);
    }

    [Fact]
    public void Create_requires_a_positive_workspace_id()
    {
        var result = CreateValidator.Validate(ValidCreate() with { WorkspaceId = 0 });

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateMaintenancePeriodRequest.WorkspaceId));
    }

    [Fact]
    public void Each_of_the_four_date_time_fields_is_required()
    {
        var create = ValidCreate();
        Assert.Contains(CreateValidator.Validate(create with { StartDate = null }).Errors, e => e.PropertyName == "StartDate");
        Assert.Contains(CreateValidator.Validate(create with { StartTime = null }).Errors, e => e.PropertyName == "StartTime");
        Assert.Contains(CreateValidator.Validate(create with { EndDate = null }).Errors, e => e.PropertyName == "EndDate");
        Assert.Contains(CreateValidator.Validate(create with { EndTime = null }).Errors, e => e.PropertyName == "EndTime");
    }

    [Fact]
    public void Start_equal_to_end_is_rejected()
    {
        var request = ValidCreate() with { EndTime = new TimeOnly(9, 0) };

        Assert.Contains("The maintenance start must be before its end.", Errors(request));
    }

    [Fact]
    public void Same_day_end_before_start_is_rejected()
    {
        var request = ValidCreate() with { StartTime = new TimeOnly(13, 0), EndTime = new TimeOnly(9, 0) };

        Assert.Contains("The maintenance start must be before its end.", Errors(request));
    }

    // Maintenance (unlike a booking) has independent start and end DATES,
    // so a period may legitimately span midnight.
    [Fact]
    public void A_period_that_spans_midnight_is_valid()
    {
        var request = ValidCreate() with
        {
            StartDate = Day, StartTime = new TimeOnly(22, 0),
            EndDate = Day.AddDays(1), EndTime = new TimeOnly(2, 0)
        };

        Assert.True(CreateValidator.Validate(request).IsValid);
    }

    // The comparison must combine date AND time, not compare TimeOnly parts:
    // here the end TIME (18:00) is later than the start time (08:00), but the
    // end DATE is a day earlier, so the interval is reversed.
    [Fact]
    public void End_date_before_start_date_is_rejected_even_when_the_end_time_is_later()
    {
        var request = ValidCreate() with
        {
            StartDate = Day.AddDays(1), StartTime = new TimeOnly(8, 0),
            EndDate = Day, EndTime = new TimeOnly(18, 0)
        };

        Assert.Contains("The maintenance start must be before its end.", Errors(request));
    }

    [Fact]
    public void Reason_longer_than_500_characters_is_rejected_and_exactly_500_is_allowed()
    {
        Assert.Contains(CreateValidator.Validate(ValidCreate() with { Reason = new string('x', 501) }).Errors,
            e => e.PropertyName == "Reason");
        Assert.True(CreateValidator.Validate(ValidCreate() with { Reason = new string('x', 500) }).IsValid);
        Assert.True(CreateValidator.Validate(ValidCreate() with { Reason = null }).IsValid);
    }

    [Fact]
    public void Start_inside_the_dst_gap_is_rejected()
    {
        var gap = DstTestSupport.FindSpringForwardGap();
        var request = ValidCreate() with
        {
            StartDate = gap.Date, StartTime = gap.InvalidTime,
            EndDate = gap.Date, EndTime = new TimeOnly(23, 0)
        };

        Assert.Contains("The maintenance start does not exist in Egypt local time (daylight-saving gap).", Errors(request));
    }

    [Fact]
    public void End_inside_the_dst_gap_is_rejected()
    {
        var gap = DstTestSupport.FindSpringForwardGap();
        var request = ValidCreate() with
        {
            StartDate = gap.Date.AddDays(-1), StartTime = new TimeOnly(9, 0),
            EndDate = gap.Date, EndTime = gap.InvalidTime
        };

        Assert.Contains("The maintenance end does not exist in Egypt local time (daylight-saving gap).", Errors(request));
    }

    // Create and Update share one rule set; if they ever diverge this fails.
    [Fact]
    public void Create_and_update_apply_the_identical_interval_and_dst_rules()
    {
        var gap = DstTestSupport.FindSpringForwardGap();

        var reversedCreate = ValidCreate() with { StartTime = new TimeOnly(13, 0), EndTime = new TimeOnly(9, 0) };
        var reversedUpdate = ValidUpdate() with { StartTime = new TimeOnly(13, 0), EndTime = new TimeOnly(9, 0) };
        Assert.Equal(Errors(reversedCreate), Errors(reversedUpdate));

        var gapCreate = ValidCreate() with { StartDate = gap.Date, StartTime = gap.InvalidTime, EndDate = gap.Date, EndTime = new TimeOnly(23, 0) };
        var gapUpdate = ValidUpdate() with { StartDate = gap.Date, StartTime = gap.InvalidTime, EndDate = gap.Date, EndTime = new TimeOnly(23, 0) };
        Assert.Equal(Errors(gapCreate), Errors(gapUpdate));
    }
}
