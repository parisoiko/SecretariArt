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
    [InlineData("Email", "not-an-email", "The Email field is not a valid e-mail address.")]
    [InlineData("ConfirmPassword", "Different!2026", "The passwords don&#x27;t match.")]
    [InlineData("Password", "short", "minimum length of 8")]
    [InlineData("FirstName", "", "The First name field is required.")]
    public async Task Register_RejectsInvalidInput(string field, string value, string expectedError)
    {
        var fields = new Dictionary<string, string>
        {
            ["FirstName"] = "Petros", ["LastName"] = "Tester", ["Email"] = $"petros.{Guid.NewGuid():N}@test.example",
            ["PhoneNumber"] = "+30 690 000 0000", ["Password"] = DbInitializer.DemoPassword, ["ConfirmPassword"] = DbInitializer.DemoPassword,
        };
        fields[field] = value;

        HttpResponseMessage response = await factory.CreateBrowser().SubmitFormAsync("/account/register", "/account/register", fields);

        Assert.Contains(expectedError, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Register_RejectsAnEmailThatIsAlreadyUsed()
    {
        HttpResponseMessage response = await factory.CreateBrowser().SubmitFormAsync("/account/register", "/account/register", new()
        {
            ["FirstName"] = "Eleni", ["LastName"] = "Again", ["Email"] = "eleni@demo.secretariart.app",
            ["PhoneNumber"] = "+30 690 000 0000", ["Password"] = DbInitializer.DemoPassword, ["ConfirmPassword"] = DbInitializer.DemoPassword,
        });

        Assert.Contains("is already taken", await response.Content.ReadAsStringAsync());
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
                ["Email"] = "giorgos@demo.secretariart.app", ["Password"] = DbInitializer.DemoPassword,
            });

        Assert.Equal("/", response.Headers.Location!.ToString());
    }
}
