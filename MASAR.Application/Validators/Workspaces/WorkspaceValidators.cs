using FluentValidation;
using Masar.Application.DTOs.Workspaces;

namespace Masar.Application.Validators.Workspaces;

public class CreateWorkspaceRequestValidator : AbstractValidator<CreateWorkspaceRequest>
{
    public CreateWorkspaceRequestValidator()
    {
        RuleFor(x => x.LocationId)
            .GreaterThan(0);

        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(150);

        RuleFor(x => x.Type)
            .IsInEnum();

        RuleFor(x => x.Capacity)
            .GreaterThan(0);

        // Same category of check as Booking's StartTime < EndTime — both
        // bounds of an interval in the wrong order is a shape-level
        // problem, not a business-state conflict, so it's folded into the
        // same VALIDATION_FAILED/400 path as everything else here rather
        // than given its own 422 error code.
        RuleFor(x => x)
            .Must(x => x.OpeningTime < x.ClosingTime)
            .WithMessage("OpeningTime must be before ClosingTime.")
            .WithName("OperatingHours");
    }
}

public class UpdateWorkspaceRequestValidator : AbstractValidator<UpdateWorkspaceRequest>
{
    public UpdateWorkspaceRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(150);

        RuleFor(x => x.Type)
            .IsInEnum();

        RuleFor(x => x.Capacity)
            .GreaterThan(0);

        RuleFor(x => x)
            .Must(x => x.OpeningTime < x.ClosingTime)
            .WithMessage("OpeningTime must be before ClosingTime.")
            .WithName("OperatingHours");
    }
}
