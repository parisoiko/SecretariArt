using System.Net;
using SecretariArt.Data;

namespace SecretariArt.Tests.Integration;

[Collection(IntegrationTests.Name)]
public class AccountTests(SecretariArtFactory factory)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/schedule")]
    [InlineData("/admin")]
    public async Task Pages_RequireSigningIn(string path)
    {
        HttpResponseMessage response = await factory.CreateBrowser(followRedirects: false).GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://localhost/account/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Register_CreatesTheAccountAndSignsIn()
    {
        HttpClient browser = factory.CreateBrowser();
        string email = await browser.RegisterNewUserAsync("Ioanna");

        Assert.Equal("Ioanna", factory.Query(db => db.Users.Single(u => u.Email == email).FirstName));
        Assert.Equal("+30 690 000 0000", factory.Query(db => db.Users.Single(u => u.Email == email).PhoneNumber));
    }

    [Theory]
    [InlineData("PhoneNumber", "abc", "Enter a phone number")]
    [InlineData("Email", "not-an-email", "Enter a valid email address")]
    [InlineData("ConfirmPassword", "Different!2026", "The passwords don&#x27;t match.")]
    [InlineData("Password", "short", "minimum length of 8")]
    [InlineData("FirstName", "", "The First name field is required.")]
    public async Task Register_RejectsInvalidInput(string field, string value, string expectedError)
    {
        var fields = new Dictionary<string, string>
        {
            ["Input.FirstName"] = "Petros", ["Input.LastName"] = "Tester", ["Input.Email"] = $"petros.{Guid.NewGuid():N}@test.example",
            ["Input.PhoneNumber"] = "+30 690 000 0000", ["Input.Password"] = DbInitializer.DemoPassword, ["Input.ConfirmPassword"] = DbInitializer.DemoPassword,
        };
        fields["Input." + field] = value;

        HttpResponseMessage response = await factory.CreateBrowser().SubmitFormAsync("/account/register", "/account/register", fields);

        Assert.Contains(expectedError, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Register_RejectsAnEmailThatIsAlreadyUsed()
    {
        HttpResponseMessage response = await factory.CreateBrowser().SubmitFormAsync("/account/register", "/account/register", new()
        {
            ["Input.FirstName"] = "Eleni", ["Input.LastName"] = "Again", ["Input.Email"] = "eleni@demo.secretariart.app",
            ["Input.PhoneNumber"] = "+30 690 000 0000", ["Input.Password"] = DbInitializer.DemoPassword, ["Input.ConfirmPassword"] = DbInitializer.DemoPassword,
        });

        string page = await response.Content.ReadAsStringAsync();
        Assert.Contains("There&#x27;s already an account with this email.", page);
        Assert.Contains("Sign in instead", page);
        Assert.Contains("account/login?email=eleni%40demo.secretariart.app", page);
    }

    [Fact]
    public async Task SignIn_WithTheWrongPassword_ShowsOneGenericMessage()
    {
        HttpResponseMessage response = await factory.CreateBrowser().SignInAsync("giorgos@demo.secretariart.app", "Wrong!2026");

        Assert.Contains("Wrong email or password.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SignIn_TakesYouToYourOrganisations()
    {
        HttpResponseMessage response = await factory.CreateBrowser().SignInAsync("giorgos@demo.secretariart.app");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Hello, Giorgos", body);
        Assert.Contains("Athens Drama School", body);
    }

    [Fact]
    public async Task SignIn_IgnoresReturnUrlsOutsideTheSite()
    {
        HttpClient browser = factory.CreateBrowser(followRedirects: false);
        HttpResponseMessage response = await browser.SubmitFormAsync("/account/login",
            "/account/login?returnUrl=https%3A%2F%2Fevil.example", new()
            {
                ["Input.Email"] = "giorgos@demo.secretariart.app", ["Input.Password"] = DbInitializer.DemoPassword,
            });

        // Blazor's redirect is absolute: this site's home page, not evil.example.
        Assert.Equal("http://localhost/", response.Headers.Location!.ToString());
    }

    // Blazor's InputText writes a field's value into the page, password fields included: the account pages clear the
    // passwords before showing a refused form again, so a typed password never comes back in the HTML.
    [Fact]
    public async Task RefusedForms_NeverSendTheTypedPasswordBack()
    {
        const string typed = "Secret!Typed2026";
        string signIn = await (await factory.CreateBrowser().SignInAsync("giorgos@demo.secretariart.app", typed)).Content.ReadAsStringAsync();
        string register = await (await factory.CreateBrowser().SubmitFormAsync("/account/register", "/account/register", new()
        {
            ["Input.Email"] = "not-an-email", ["Input.Password"] = typed, ["Input.ConfirmPassword"] = typed,
        })).Content.ReadAsStringAsync();

        Assert.Contains("Wrong email or password.", signIn);
        Assert.Contains("<label for=\"password\" class=\"form-label\">Password</label>", signIn);
        Assert.DoesNotContain(typed, signIn);
        Assert.Contains("Enter a valid email address", register);
        Assert.DoesNotContain(typed, register);
    }
}
