using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SecretariArt.Models;

namespace SecretariArt.Services;

public interface IPasswordResetService
{
    // Emails a reset link if the address has an account; does nothing otherwise (the caller answers the same either
    // way, so the form can't be used to find out who has an account). siteBaseUrl is the request's own address
    // ("https://host/"), used only when Email:PublicUrl isn't set.
    Task SendLinkAsync(string email, string siteBaseUrl);
}

public class PasswordResetService(
    UserManager<ApplicationUser> users, IEmailSender email, IOptions<EmailSettings> emailSettings) : IPasswordResetService
{
    public async Task SendLinkAsync(string address, string siteBaseUrl)
    {
        if (await users.FindByEmailAsync(address.Trim()) is not ApplicationUser user)
        {
            return;
        }

        string token = await users.GeneratePasswordResetTokenAsync(user);
        string query = $"email={Uri.EscapeDataString(user.Email!)}&code={EncodeCode(token)}";
        // Links in emails point at the configured public address when there is one, not at whatever Host the request
        // claimed (see EmailSettings.PublicUrl).
        string baseUrl = emailSettings.Value.PublicUrl is { Length: > 0 } publicUrl ? publicUrl : siteBaseUrl;
        string link = $"{baseUrl.TrimEnd('/')}/account/reset-password?{query}";

        await email.SendAsync(new EmailMessage(user.Email!, "Reset your SecretariArt password", $"""
            Hello {user.FirstName},

            Someone (hopefully you) asked to reset the password of your SecretariArt account.
            To choose a new password, open this link within two hours:

            {link}

            If you didn't ask for this, ignore this email: your password stays as it is.
            """));
    }

    // The token, Base64Url-encoded so it survives the query string.
    public static string EncodeCode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    public static string DecodeCode(string code)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        }
        catch (FormatException)
        {
            return "";  // A mangled link: Identity reports it as an invalid token.
        }
    }
}
