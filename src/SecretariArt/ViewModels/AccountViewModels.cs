using System.ComponentModel.DataAnnotations;

namespace SecretariArt.ViewModels;

// Validation wording shared by several forms. The framework's default for [EmailAddress] says "e-mail".
public static class Messages
{
    public const string InvalidEmail = "Enter a valid email address, e.g. name@example.com.";
}

public class RegisterViewModel
{
    // [Required] is explicit everywhere: the implicit "non-nullable means required" rule is skipped
    // when a field is missing from the posted form.
    [Required, StringLength(100), Display(Name = "First name")]
    public string FirstName { get; set; } = "";

    [Required, StringLength(100), Display(Name = "Last name")]
    public string LastName { get; set; } = "";

    [Required, EmailAddress(ErrorMessage = Messages.InvalidEmail), StringLength(256)]
    public string Email { get; set; } = "";

    [Required, Display(Name = "Phone number")]
    [RegularExpression(@"^\+?[0-9][0-9 ()\-]{6,19}$",
        ErrorMessage = "Enter a phone number with 7 to 20 digits, e.g. +30 690 123 4567.")]
    public string PhoneNumber { get; set; } = "";

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 8)]
    public string Password { get; set; } = "";

    [Required, DataType(DataType.Password), Display(Name = "Confirm password")]
    [Compare(nameof(Password), ErrorMessage = "The passwords don't match.")]
    public string ConfirmPassword { get; set; } = "";
}

public class LoginViewModel
{
    [Required, EmailAddress(ErrorMessage = Messages.InvalidEmail)]
    public string Email { get; set; } = "";

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [Display(Name = "Keep me signed in")]
    public bool RememberMe { get; set; }
}
