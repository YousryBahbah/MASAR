using Masar.Application.DTOs.Auth;
using Masar.Application.Validators.Auth;
using Xunit;

namespace Masar.UnitTests;

public class AuthValidatorsTests
{
    private static readonly RegisterRequestValidator Register = new();
    private static readonly LoginRequestValidator Login = new();

    private static RegisterRequest ValidRegister() => new("Sara", "Hassan", "sara@example.com", "Passw0rd!x");

    [Fact]
    public void A_valid_registration_passes()
    {
        Assert.True(Register.Validate(ValidRegister()).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void First_and_last_name_are_required(string? value)
    {
        Assert.Contains(Register.Validate(ValidRegister() with { FirstName = value! }).Errors, e => e.PropertyName == "FirstName");
        Assert.Contains(Register.Validate(ValidRegister() with { LastName = value! }).Errors, e => e.PropertyName == "LastName");
    }

    [Fact]
    public void Names_longer_than_100_characters_are_rejected()
    {
        var tooLong = new string('a', 101);

        Assert.Contains(Register.Validate(ValidRegister() with { FirstName = tooLong }).Errors, e => e.PropertyName == "FirstName");
        Assert.Contains(Register.Validate(ValidRegister() with { LastName = tooLong }).Errors, e => e.PropertyName == "LastName");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing-at.example.com")]
    public void A_malformed_email_is_rejected(string email)
    {
        Assert.Contains(Register.Validate(ValidRegister() with { Email = email }).Errors, e => e.PropertyName == "Email");
    }

    [Theory]
    [InlineData("")]
    [InlineData("short1!")]
    public void A_password_shorter_than_eight_characters_is_rejected(string password)
    {
        Assert.Contains(Register.Validate(ValidRegister() with { Password = password }).Errors, e => e.PropertyName == "Password");
    }

    [Fact]
    public void A_valid_login_passes()
    {
        Assert.True(Login.Validate(new LoginRequest("sara@example.com", "anything")).IsValid);
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("not-an-email", "pw")]
    [InlineData("sara@example.com", "")]
    public void An_incomplete_login_is_rejected(string email, string password)
    {
        Assert.False(Login.Validate(new LoginRequest(email, password)).IsValid);
    }
}
