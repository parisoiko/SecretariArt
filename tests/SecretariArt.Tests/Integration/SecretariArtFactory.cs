using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SecretariArt.Data;
using SecretariArt.Services;

namespace SecretariArt.Tests.Integration;

// The whole app in memory, on an in-memory SQLite database instead of SQL Server. Development environment,
// so the platform admin and the demo organisations are seeded (see DbInitializer).
public class SecretariArtFactory : WebApplicationFactory<Program>
{
    protected virtual IEnumerable<(string Limit, int Permits)> Limits =>
        new[] { "Global", "SignIn", "Register", "InviteCodes", "OrganizationRequests", "Generation", "AccountRecovery" }.Select(l => (l, 100_000));

    // An in-memory SQLite database lives as long as one connection to it stays open.
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public SecretariArtFactory() => _connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Billing runs when pages ask for it; the hourly worker would share the one SQLite connection at random times.
        builder.UseSetting("Billing:RunInBackground", "false");
        // Every test comes from the same in-memory address and signs in many times: limits high enough never to
        // get in the way (RateLimitTests checks them with low ones).
        foreach ((string limit, int permits) in Limits)
        {
            builder.UseSetting($"RateLimits:{limit}:PermitLimit", permits.ToString());
        }
        builder.ConfigureServices(services =>
        {
            // Since EF Core 8 the provider is configured through IDbContextOptionsConfiguration<T>: remove it too,
            // or SQL Server would still be configured next to SQLite.
            services.RemoveAll<DbContextOptions<SecretariArtDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<SecretariArtDbContext>>();
            services.AddDbContext<SecretariArtDbContext>(options => options.UseSqlite(_connection));
            // Emails are kept here for the tests to read, instead of written to App_Data.
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }

    public CapturedEmails Emails { get; } = new();

    public HttpClient CreateBrowser(bool followRedirects = true) =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = followRedirects });

    public T Query<T>(Func<SecretariArtDbContext, T> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<SecretariArtDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}

// One app and one database for all integration tests: tests use their own new users, so they don't interfere.
[CollectionDefinition(Name)]
public class IntegrationTests : ICollectionFixture<SecretariArtFactory>
{
    public const string Name = "Integration tests";
}

public static class BrowserExtensions
{
    private const string TokenField = "__RequestVerificationToken";
    private const string HandlerField = "_handler";
    private static readonly Regex Forms = new(@"<form\b([^>]*)>(.*?)</form>", RegexOptions.Singleline);
    private static readonly Regex Inputs = new(@"<input\b[^>]*>");
    private static readonly Regex Attribute = new("""\b(name|value|action)="([^"]*)" """.TrimEnd());

    // Like a person filling in a form: load the page, find the form being submitted, then post its hidden fields
    // (anti-forgery token, and for a Blazor form its name, "_handler") with the given fields.
    // The form is the one named by a "_handler" field, if given (several Blazor forms post to the same page), or else
    // the one whose action is postUrl. A page with neither falls back to its first token.
    public static async Task<HttpResponseMessage> SubmitFormAsync(
        this HttpClient client, string pageUrl, string postUrl, Dictionary<string, string> fields)
    {
        string page = await client.GetStringAsync(pageUrl);
        string postPath = PathOf(postUrl);
        var forms = Forms.Matches(page)
            .Select(m => (Action: Attr(m.Groups[1].Value, "action"), Hidden: HiddenFields(m.Groups[2].Value)))
            .Where(f => f.Hidden.ContainsKey(TokenField))  // only POST forms carry one; GET forms (filters) can share an action
            .ToList();
        var chosen = fields.TryGetValue(HandlerField, out string? handler)
            ? forms.FirstOrDefault(f => f.Hidden.GetValueOrDefault(HandlerField) == handler)
            : forms.FirstOrDefault(f => PathOf(f.Action ?? pageUrl) == postPath);

        Dictionary<string, string> hidden = chosen.Hidden
            ?? HiddenFields(page).Where(h => h.Key == TokenField).ToDictionary();
        Assert.True(hidden.ContainsKey(TokenField), $"No anti-forgery token for {postUrl} on {pageUrl}.");

        var form = new Dictionary<string, string>(fields);
        foreach (string name in new[] { TokenField, HandlerField })
        {
            if (hidden.TryGetValue(name, out string? value))
            {
                form.TryAdd(name, value);
            }
        }
        return await client.PostAsync(postUrl, new FormUrlEncodedContent(form));
    }

    // The anti-forgery token and Blazor's form name among a form's inputs (attributes in any order).
    private static Dictionary<string, string> HiddenFields(string html) =>
        Inputs.Matches(html)
            .Select(m => (Name: Attr(m.Value, "name"), Value: Attr(m.Value, "value")))
            .Where(i => i.Name is TokenField or HandlerField && i.Value != null)
            .DistinctBy(i => i.Name)
            .ToDictionary(i => i.Name!, i => i.Value!);

    private static string? Attr(string tag, string name) =>
        Attribute.Matches(tag).FirstOrDefault(m => m.Groups[1].Value == name) is { } m ? WebUtility.HtmlDecode(m.Groups[2].Value) : null;

    // "/account/login?returnUrl=…" → "/account/login"; Blazor's relative actions ("account/logout") are site-relative.
    private static string PathOf(string url)
    {
        string path = url.Split('?', '#')[0];
        return path.StartsWith('/') ? path : "/" + path;
    }

    public static Task<HttpResponseMessage> SignInAsync(this HttpClient client, string email, string password = DbInitializer.DemoPassword) =>
        client.SubmitFormAsync("/account/login", "/account/login", new() { ["Input.Email"] = email, ["Input.Password"] = password });

    // Registers a brand-new user (and signs them in). Returns their email.
    public static async Task<string> RegisterNewUserAsync(this HttpClient client, string firstName = "Anna")
    {
        string email = $"{firstName.ToLowerInvariant()}.{Guid.NewGuid():N}@test.example";
        HttpResponseMessage response = await client.SubmitFormAsync("/account/register", "/account/register", new()
        {
            ["Input.FirstName"] = firstName,
            ["Input.LastName"] = "Tester",
            ["Input.Email"] = email,
            ["Input.PhoneNumber"] = "+30 690 000 0000",
            ["Input.Password"] = DbInitializer.DemoPassword,
            ["Input.ConfirmPassword"] = DbInitializer.DemoPassword,
        });
        // A new account belongs to no organisation yet: the welcome page.
        Assert.Contains($"Welcome, {firstName}", await response.Content.ReadAsStringAsync());
        return email;
    }
}

public class CapturedEmails : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        lock (_sent) _sent.Add(message);
        return Task.CompletedTask;
    }

    public List<EmailMessage> To(string email)
    {
        lock (_sent) return _sent.Where(m => m.To == email).ToList();
    }
}
