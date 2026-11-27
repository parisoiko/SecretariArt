using System.Net;

namespace SecretariArt.Tests.Integration;

// The app with one tiny limit at a time, to see each limit stop a script: sign-in, registration and invite codes.
// Each has its own app and database (limits count per address, and every test client shares one), so neither the
// shared app (limits out of the way) nor the other limits' tests are affected.
public class TinyLimitFactory(string limit, int permits) : SecretariArtFactory
{
    protected override IEnumerable<(string Limit, int Permits)> Limits =>
        base.Limits.Select(l => l.Limit == limit ? (l.Limit, permits) : l);
}

public class SignInLimitFactory() : TinyLimitFactory("SignIn", 3);
public class RegisterLimitFactory() : TinyLimitFactory("Register", 2);
public class InviteCodeLimitFactory() : TinyLimitFactory("InviteCodes", 2);

public class SignInLimitTests(SignInLimitFactory factory) : IClassFixture<SignInLimitFactory>
{
    [Fact]
    public async Task GuessingPasswords_IsStopped_WithAPageThatSaysHowLongToWait()
    {
        HttpClient browser = factory.CreateBrowser();
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await browser.SignInAsync("nobody@test.example", "Wrong!2026")).StatusCode);
        }

        HttpResponseMessage stopped = await browser.SignInAsync("nobody@test.example", "Wrong!2026");

        Assert.Equal(HttpStatusCode.TooManyRequests, stopped.StatusCode);
        Assert.True(stopped.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        string page = WebUtility.HtmlDecode(await stopped.Content.ReadAsStringAsync());
        Assert.Contains("Too many attempts", page);
        Assert.Contains("Please wait a minute and try again.", page);

        // A script posting without the form's token gets the same answer, not a 400 from the error page itself.
        HttpResponseMessage scripted = await browser.PostAsync("/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "x", ["Password"] = "x" }));
        Assert.Equal(HttpStatusCode.TooManyRequests, scripted.StatusCode);
        Assert.Contains("Too many attempts", await scripted.Content.ReadAsStringAsync());
    }
}

public class RegisterLimitTests(RegisterLimitFactory factory) : IClassFixture<RegisterLimitFactory>
{
    [Fact]
    public async Task RegisteringManyAccountsFromOneAddress_IsStopped()
    {
        HttpClient browser = factory.CreateBrowser();
        var empty = new Dictionary<string, string> { ["Input.FirstName"] = "", ["Input.Email"] = "" };

        Assert.Equal(HttpStatusCode.OK, (await browser.SubmitFormAsync("/account/register", "/account/register", empty)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.SubmitFormAsync("/account/register", "/account/register", empty)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.SubmitFormAsync("/account/register", "/account/register", empty)).StatusCode);
    }
}

public class InviteCodeLimitTests(InviteCodeLimitFactory factory) : IClassFixture<InviteCodeLimitFactory>
{
    [Fact]
    public async Task GuessingInviteCodes_IsStoppedPerPerson()
    {
        HttpClient browser = factory.CreateBrowser();
        await browser.RegisterNewUserAsync("Guesser");

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/join/AAAA2222")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/join/BBBB3333")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.GetAsync("/join/CCCC4444")).StatusCode);

        // Someone else is counted on their own, even from the same address.
        HttpClient other = factory.CreateBrowser();
        await other.RegisterNewUserAsync("Honest");
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/join/ATHENS26")).StatusCode);
    }
}

public class RecoveryLimitFactory() : TinyLimitFactory("AccountRecovery", 2);

// "Forgot your password?" is a Blazor page: the limit is on the page, but only posting the form counts.
public class RecoveryLimitTests(RecoveryLimitFactory factory) : IClassFixture<RecoveryLimitFactory>
{
    [Fact]
    public async Task AskingForResetLinks_IsStopped_ButOpeningThePageIsNot()
    {
        HttpClient browser = factory.CreateBrowser();
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account/forgot-password")).StatusCode);
        }

        var form = new Dictionary<string, string> { ["Input.Email"] = "nobody@test.example" };
        Assert.Equal(HttpStatusCode.OK, (await browser.SubmitFormAsync("/account/forgot-password", "/account/forgot-password", form)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.SubmitFormAsync("/account/forgot-password", "/account/forgot-password", form)).StatusCode);

        HttpResponseMessage stopped = await browser.SubmitFormAsync("/account/forgot-password", "/account/forgot-password", form);
        Assert.Equal(HttpStatusCode.TooManyRequests, stopped.StatusCode);
        Assert.Contains("Too many attempts", await stopped.Content.ReadAsStringAsync());
    }
}
