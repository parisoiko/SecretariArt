using System.Net;

namespace SecretariArt.Tests.Integration;

[Collection(IntegrationTests.Name)]
public class HostingTests(SecretariArtFactory factory)
{
    [Fact]
    public async Task Health_AnswersWithoutSigningIn_WhenTheDatabaseIsReachable()
    {
        HttpResponseMessage response = await factory.CreateBrowser(followRedirects: false).GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    // Error pages are a Blazor page (Components/Pages/Error.razor), reached by re-executing the failed request.
    [Fact]
    public async Task ErrorPages_ShowTheFriendlyPage_ForVisitorsAndSignedInPeople()
    {
        HttpResponseMessage visitor = await factory.CreateBrowser().GetAsync("/error/404");
        Assert.Equal(HttpStatusCode.NotFound, visitor.StatusCode);
        Assert.Contains("We couldn&#x27;t find that page", await visitor.Content.ReadAsStringAsync());

        // Another organisation's classroom: the admin pages answer 404, shown as the same page.
        HttpClient nikos = factory.CreateBrowser();
        await nikos.SignInAsync("nikos@demo.secretariart.app");
        int patras = factory.Query(db => db.Organizations.Single(o => o.Name == "Patras Dance Studio").Id);
        int athensRoom = factory.Query(db => db.Classrooms.First(c => c.Organization!.Name == "Athens Drama School").Id);
        HttpResponseMessage missing = await nikos.GetAsync($"/admin/{patras}/classrooms/{athensRoom}/edit");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        string page = await missing.Content.ReadAsStringAsync();
        Assert.Contains("We couldn&#x27;t find that page", page);
        Assert.Contains("<title>Not found · SecretariArt</title>", page);
    }
}
