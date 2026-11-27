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

namespace SecretariArt.Tests.Integration;

// The whole app in memory, on an in-memory SQLite database instead of SQL Server. Development environment,
// so the platform admin and the demo organisations are seeded (see DbInitializer).
public class SecretariArtFactory : WebApplicationFactory<Program>
{
    protected virtual IEnumerable<(string Limit, int Permits)> Limits =>
        new[] { "Global", "SignIn", "Register", "InviteCodes", "OrganizationRequests", "Generation" }.Select(l => (l, 100_000));

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
        });
    }

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
    private static readonly Regex AntiforgeryToken = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");

    // Like a person filling in a form: load the page (for its anti-forgery token), then post the fields.
    public static async Task<HttpResponseMessage> SubmitFormAsync(
        this HttpClient client, string pageUrl, string postUrl, Dictionary<string, string> fields)
    {
        string page = await client.GetStringAsync(pageUrl);
        Match token = AntiforgeryToken.Match(page);
        Assert.True(token.Success, $"No anti-forgery token on {pageUrl}.");

        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value) };
        return await client.PostAsync(postUrl, new FormUrlEncodedContent(form));
    }

    public static Task<HttpResponseMessage> SignInAsync(this HttpClient client, string email, string password = DbInitializer.DemoPassword) =>
        client.SubmitFormAsync("/account/login", "/account/login", new() { ["Email"] = email, ["Password"] = password });

    // Registers a brand-new user (and signs them in). Returns their email.
    public static async Task<string> RegisterNewUserAsync(this HttpClient client, string firstName = "Anna")
    {
        string email = $"{firstName.ToLowerInvariant()}.{Guid.NewGuid():N}@test.example";
        HttpResponseMessage response = await client.SubmitFormAsync("/account/register", "/account/register", new()
        {
            ["FirstName"] = firstName,
            ["LastName"] = "Tester",
            ["Email"] = email,
            ["PhoneNumber"] = "+30 690 000 0000",
            ["Password"] = DbInitializer.DemoPassword,
            ["ConfirmPassword"] = DbInitializer.DemoPassword,
        });
        // A new account belongs to no organisation yet: the welcome page.
        Assert.Contains($"Welcome, {firstName}", await response.Content.ReadAsStringAsync());
        return email;
    }
}
