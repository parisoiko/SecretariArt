using System.Text;
using Microsoft.Extensions.Options;

namespace SecretariArt.Services;

public record EmailMessage(string To, string Subject, string Body);

// How the app sends email. The only implementation writes messages to a folder (see below); a real provider
// (SMTP, SendGrid, …) is one more class implementing this interface, registered in ServiceCollectionExtensions.
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

public class EmailSettings
{
    // Where the folder sender drops messages, relative to the app's content root.
    public string PickupDirectory { get; set; } = "App_Data/mail";

    // The site's public address, used for links in emails (e.g. "https://secretariart.example"). When empty, links
    // use the address of the request; set it in production, so a forged Host header can't redirect a reset link.
    public string? PublicUrl { get; set; }
}

// Saves each message as an .eml file (opens in any mail client) instead of sending it: enough for development and
// demos, where nobody should receive real email. The log says where the file went, without the address.
public class PickupDirectoryEmailSender(
    IOptions<EmailSettings> settings,
    IWebHostEnvironment environment,
    TimeProvider clock,
    ILogger<PickupDirectoryEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        string folder = Path.Combine(environment.ContentRootPath, settings.Value.PickupDirectory);
        Directory.CreateDirectory(folder);

        DateTimeOffset now = clock.GetUtcNow();
        string path = Path.Combine(folder, $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.eml");
        string eml = $"""
            From: SecretariArt <no-reply@secretariart.app>
            To: {message.To}
            Subject: {message.Subject}
            Date: {now:R}
            Content-Type: text/plain; charset=utf-8

            {message.Body}
            """;
        await File.WriteAllTextAsync(path, eml, Encoding.UTF8, cancellationToken);
        logger.LogInformation("Email \"{Subject}\" saved to {Path}.", message.Subject, path);
    }
}
