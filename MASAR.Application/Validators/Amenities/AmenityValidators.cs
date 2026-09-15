using FluentValidation;
using Masar.Application.DTOs.Amenities;

namespace Masar.Application.Validators.Amenities;

public class CreateAmenityRequestValidator : AbstractValidator<CreateAmenityRequest>
{
    public CreateAmenityRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(100);
    }
}

public class UpdateWorkspaceAmenitiesRequestValidator : AbstractValidator<UpdateWorkspaceAmenitiesRequest>
{
    public UpdateWorkspaceAmenitiesRequestValidator()
    {
        // Null vs empty are different requests: null is a malformed body
        // (VALIDATION_FAILED), an empty list is a valid request meaning
        // "this workspace should have no amenities" — so only NotNull
        // here, no MinimumLength/NotEmpty on the list itself.
        RuleFor(x => x.AmenityIds).NotNull();
    }
}
