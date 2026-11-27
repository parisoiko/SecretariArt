using System.Net;
using System.Text.RegularExpressions;
using SecretariArt.Data;
using SecretariArt.Services;

namespace SecretariArt.Tests.Integration;

// "Forgot password", "My account" and the security headers, through the real forms.
[Collection(IntegrationTests.Name)]
public class AccountSelfServiceTests(SecretariArtFactory factory)
{
    private const string NewPassword = "Brand!New2026";

    [Fact]
    public async Task ForgotPassword_EmailsALinkThatSetsANewPassword()
    {
        HttpClient owner = factory.CreateBrowser();
        string email = await owner.RegisterNewUserAsync("Rena");

        HttpClient visitor = factory.CreateBrowser();
        HttpResponseMessage sent = await visitor.SubmitFormAsync("/account/forgot-password", "/account/forgot-password", new() { ["Email"] = email });
        Assert.Contains("Check your email", await sent.Content.ReadAsStringAsync());

        string link = ResetLink(Assert.Single(factory.Emails.To(email)));
        HttpResponseMessage reset = await visitor.SubmitFormAsync(link, "/account/reset-password", ResetFields(link, NewPassword));
        Assert.Contains("Your password has been changed", await reset.Content.ReadAsStringAsync());

        // The old password no longer works; the new one does.
        Assert.Contains("Wrong email or password", await (await factory.CreateBrowser().SignInAsync(email)).Content.ReadAsStringAsync());
        Assert.Contains("Welcome, Rena", await (await factory.CreateBrowser().SignInAsync(email, NewPassword)).Content.ReadAsStringAsync());

        // A link works once.
        HttpResponseMessage again = await visitor.SubmitFormAsync(link, "/account/reset-password", ResetFields(link, "Another!2026"));
        Assert.Contains("has expired or has already been used", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ForgotPassword_AnswersTheSameForAnUnknownEmail_AndSendsNothing()
    {
        string email = $"nobody.{Guid.NewGuid():N}@test.example";
        HttpResponseMessage sent = await factory.CreateBrowser()
            .SubmitFormAsync("/account/forgot-password", "/account/forgot-password", new() { ["Email"] = email });

        Assert.Contains("Check your email", await sent.Content.ReadAsStringAsync());
        Assert.Empty(factory.Emails.To(email));
    }

    [Fact]
    public async Task ResetPassword_WithAForgedCode_IsRefused()
    {
        HttpClient owner = factory.CreateBrowser();
        string email = await owner.RegisterNewUserAsync("Fotis");
        string link = $"/account/reset-password?email={Uri.EscapeDataString(email)}&code=bm90LWEtdG9rZW4";

        HttpResponseMessage reset = await factory.CreateBrowser().SubmitFormAsync(link, "/account/reset-password", ResetFields(link, NewPassword));

        Assert.Contains("has expired or has already been used", await reset.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResetPassword_UnlocksALockedAccount()
    {
        HttpClient owner = factory.CreateBrowser();
        string email = await owner.RegisterNewUserAsync("Lena");
        for (int i = 0; i < 5; i++)
        {
            await factory.CreateBrowser().SignInAsync(email, "Wrong!2026");
        }
        Assert.Contains("Too many failed attempts", await (await factory.CreateBrowser().SignInAsync(email)).Content.ReadAsStringAsync());

        await factory.CreateBrowser().SubmitFormAsync("/account/forgot-password", "/account/forgot-password", new() { ["Email"] = email });
        string link = ResetLink(factory.Emails.To(email).Last());
        await factory.CreateBrowser().SubmitFormAsync(link, "/account/reset-password", ResetFields(link, NewPassword));

        Assert.Contains("Welcome, Lena", await (await factory.CreateBrowser().SignInAsync(email, NewPassword)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MyAccount_SavesNewDetails_AndTheMenuShowsTheNewName()
    {
        HttpClient browser = factory.CreateBrowser();
        string email = await browser.RegisterNewUserAsync("Petra");

        HttpResponseMessage saved = await browser.SubmitFormAsync("/account/profile", "/account/profile", new()
        {
            ["Profile.FirstName"] = "Petroula",
            ["Profile.LastName"] = "Nikolaou",
            ["Profile.PhoneNumber"] = "+30 210 123 4567",
        });

        string page = await saved.Content.ReadAsStringAsync();
        Assert.Contains("Your details have been saved.", page);
        Assert.Contains("Account menu for Petroula", page);
        Assert.Equal(("Petroula", "Nikolaou", "+30 210 123 4567"),
            factory.Query(db => db.Users.Where(u => u.Email == email).Select(u => new ValueTuple<string, string, string?>(u.FirstName, u.LastName, u.PhoneNumber)).Single()));
    }

    [Fact]
    public async Task MyAccount_ChangesThePassword_OnlyWithTheCurrentOne()
    {
        HttpClient browser = factory.CreateBrowser();
        string email = await browser.RegisterNewUserAsync("Kostas");

        HttpResponseMessage wrong = await browser.SubmitFormAsync("/account/profile", "/account/profile/password", PasswordFields("Wrong!2026"));
        Assert.Contains("That isn&#x27;t your current password.", await wrong.Content.ReadAsStringAsync());

        HttpResponseMessage changed = await browser.SubmitFormAsync("/account/profile", "/account/profile/password", PasswordFields(DbInitializer.DemoPassword));
        Assert.Contains("Your password has been changed.", await changed.Content.ReadAsStringAsync());
        Assert.Contains("Welcome, Kostas", await (await factory.CreateBrowser().SignInAsync(email, NewPassword)).Content.ReadAsStringAsync());

        // This browser stays signed in.
        Assert.Contains("My account", await browser.GetStringAsync("/account/profile"));
    }

    // After a refused change the address bar shows /account/profile/password; opening it again must not fail (QA, A2).
    [Fact]
    public async Task MyAccount_PasswordAddress_OpenedDirectly_GoesBackToThePage()
    {
        HttpClient browser = factory.CreateBrowser();
        await browser.RegisterNewUserAsync("Nefeli");

        HttpResponseMessage response = await browser.GetAsync("/account/profile/password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/account/profile", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    // "Skip to content" is the first link on every signed-in page; on admin pages it skips the sidebar too (QA, A5, A6).
    [Fact]
    public async Task SkipLink_LeadsPastTheHeader_AndPastTheAdminSidebar()
    {
        HttpClient browser = factory.CreateBrowser();
        await browser.SignInAsync("eleni@demo.secretariart.app");
        int athens = factory.Query(db => db.Organizations.Single(o => o.Name == "Athens Drama School").Id);

        string home = await browser.GetStringAsync("/");
        string admin = await browser.GetStringAsync($"/admin/{athens}/courses");

        Assert.Contains("<a class=\"skip-link\" href=\"#content\">", home);
        Assert.Contains("id=\"content\"", home);
        Assert.Contains("<a class=\"skip-link\" href=\"#admin-content\">", admin);
        Assert.Contains("id=\"admin-content\"", admin);
    }

    [Fact]
    public async Task Pages_SendSecurityHeaders_AndOurInlineScriptsCarryTheNonce()
    {
        HttpClient browser = factory.CreateBrowser();
        await browser.SignInAsync("eleni@demo.secretariart.app");
        int athens = factory.Query(db => db.Organizations.Single(o => o.Name == "Athens Drama School").Id);

        HttpResponseMessage response = await browser.GetAsync($"/admin/{athens}/members");

        string policy = response.Headers.GetValues("Content-Security-Policy").Single();
        string nonce = Regex.Match(policy, "'nonce-([^']+)'").Groups[1].Value;
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains($"<script nonce=\"{nonce}\"", await response.Content.ReadAsStringAsync());
    }

    private static string ResetLink(EmailMessage message) =>
        new Uri(Regex.Match(message.Body, @"http\S+/account/reset-password\S+").Value).PathAndQuery;

    private static Dictionary<string, string> ResetFields(string link, string password)
    {
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + link).Query);
        return new()
        {
            ["Email"] = query["email"]!, ["Code"] = query["code"]!, ["Password"] = password, ["ConfirmPassword"] = password,
        };
    }

    private static Dictionary<string, string> PasswordFields(string current) => new()
    {
        ["Password.CurrentPassword"] = current,
        ["Password.NewPassword"] = NewPassword,
        ["Password.ConfirmPassword"] = NewPassword,
    };
}
