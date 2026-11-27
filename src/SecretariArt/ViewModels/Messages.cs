namespace SecretariArt.ViewModels;

// Validation wording and patterns shared by several forms. The framework's default for [EmailAddress] says "e-mail".
public static class Messages
{
    public const string InvalidEmail = "Enter a valid email address, e.g. name@example.com.";

    // 7 to 20 digits, with spaces, brackets, dashes and a leading + allowed.
    public const string PhonePattern = @"^\+?[0-9][0-9 ()\-]{6,19}$";
    public const string InvalidPhone = "Enter a phone number with 7 to 20 digits, e.g. +30 690 123 4567.";
}
