using System.ComponentModel.DataAnnotations;

namespace SecretariArt.ViewModels;

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
    [RegularExpression(Messages.PhonePattern, ErrorMessage = Messages.InvalidPhone)]
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

public class ForgotPasswordViewModel
{
    [Required, EmailAddress(ErrorMessage = Messages.InvalidEmail)]
    public string Email { get; set; } = "";
}

public class ResetPasswordViewModel
{
    [Required]
    public string Email { get; set; } = "";

    // The token from the emailed link, Base64Url-encoded so it survives the query string.
    [Required]
    public string Code { get; set; } = "";

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 8), Display(Name = "New password")]
    public string Password { get; set; } = "";

    [Required, DataType(DataType.Password), Display(Name = "Confirm new password")]
    [Compare(nameof(Password), ErrorMessage = "The passwords don't match.")]
    public string ConfirmPassword { get; set; } = "";
}

// "My account": the two forms on one page, posted separately (each with its own field prefix).
public class AccountPageViewModel
{
    public string Email { get; set; } = "";
    public ProfileViewModel Profile { get; set; } = new();
    public ChangePasswordViewModel Password { get; set; } = new();
}

public class ProfileViewModel
{
    [Required, StringLength(100), Display(Name = "First name")]
    public string FirstName { get; set; } = "";

    [Required, StringLength(100), Display(Name = "Last name")]
    public string LastName { get; set; } = "";

    [Required, Display(Name = "Phone number")]
    [RegularExpression(Messages.PhonePattern, ErrorMessage = Messages.InvalidPhone)]
    public string PhoneNumber { get; set; } = "";
}

public class ChangePasswordViewModel
{
    [Required, DataType(DataType.Password), Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = "";

    [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 8), Display(Name = "New password")]
    public string NewPassword { get; set; } = "";

    [Required, DataType(DataType.Password), Display(Name = "Confirm new password")]
    [Compare(nameof(NewPassword), ErrorMessage = "The passwords don't match.")]
    public string ConfirmPassword { get; set; } = "";
}
