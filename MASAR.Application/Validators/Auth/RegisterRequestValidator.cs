using FluentValidation;
using Masar.Application.DTOs.Auth;

namespace Masar.Application.Validators.Auth;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.FirstName)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.LastName)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(256);

        // Length/complexity here is a first pass — Identity's own
        // PasswordOptions (configured in Program.cs) is the actual
        // source of truth and re-validates on RegisterAsync regardless.
        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8);
    }
}
