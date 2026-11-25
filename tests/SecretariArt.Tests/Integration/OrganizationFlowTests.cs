using System.Net;
using SecretariArt.Models;

namespace SecretariArt.Tests.Integration;

// The two journeys of milestone 1: an organisation is requested and approved; people join it with its invite code.
[Collection(IntegrationTests.Name)]
public class OrganizationFlowTests(SecretariArtFactory factory)
{
    private const string PlatformAdmin = "platform@demo.secretariart.app";

    private int OrganizationId(string name) => factory.Query(db => db.Organizations.Single(o => o.Name == name).Id);

    [Fact]
    public async Task RequestedOrganisation_IsApprovedByThePlatformAdmin_AndTheRequesterBecomesItsAdmin()
    {
        HttpClient owner = factory.CreateBrowser();
        await owner.RegisterNewUserAsync("Marina");
        string name = $"Studio {Guid.NewGuid():N}"[..20];

        HttpResponseMessage sent = await owner.SubmitFormAsync("/organizations/request", "/organizations/request", new()
        {
            ["Name"] = name, ["Type"] = nameof(OrganizationType.DanceSchool), ["City"] = "Volos",
            ["ContactEmail"] = "office@studio.example",
        });
        string home = await sent.Content.ReadAsStringAsync();
        Assert.Contains("We&#x27;ve received your request", home);
        Assert.Contains("Under review", home);

        HttpClient platform = factory.CreateBrowser();
        await platform.SignInAsync(PlatformAdmin);
        Assert.Contains(name, await platform.GetStringAsync("/platform/organizations"));
        int id = OrganizationId(name);
        await platform.SubmitFormAsync("/platform/organizations", $"/platform/organizations/{id}/approve", []);

        Organization approved = factory.Query(db => db.Organizations.Single(o => o.Id == id));
        Assert.Equal(OrganizationStatus.Active, approved.Status);
        Assert.Matches("^[A-Z2-9]{8}$", approved.InviteCode);

        string ownerHome = await owner.GetStringAsync("/");
        Assert.Contains(name, ownerHome);
        Assert.Contains(">Admin<", ownerHome);
        Assert.Contains("Administration", ownerHome);
        Assert.Contains(approved.InviteCode!, await owner.GetStringAsync($"/admin/{id}/members"));
    }

    [Fact]
    public async Task PlatformPages_AreOnlyForPlatformAdmins()
    {
        HttpClient browser = factory.CreateBrowser();
        await browser.SignInAsync("eleni@demo.secretariart.app");

        HttpResponseMessage response = await browser.GetAsync("/platform/organizations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the access-denied page
        Assert.Contains("have access to this page", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task JoiningWithTheInviteCode_WaitsForTheAdmin_ThenGivesAccess()
    {
        HttpClient student = factory.CreateBrowser();
        string email = await student.RegisterNewUserAsync("Lydia");

        HttpResponseMessage sent = await student.SubmitFormAsync("/join", "/join", new()
        {
            ["InviteCode"] = "athens26", ["Role"] = nameof(MembershipRole.Student),
        });
        string home = await sent.Content.ReadAsStringAsync();
        Assert.Contains("Athens Drama School will confirm your membership", home);
        Assert.Contains("Waiting for approval", home);
        Assert.DoesNotContain("href=\"/schedule\"", home);

        HttpClient admin = factory.CreateBrowser();
        await admin.SignInAsync("eleni@demo.secretariart.app");
        int athens = OrganizationId("Athens Drama School");
        Assert.Contains("Lydia Tester", await admin.GetStringAsync($"/admin/{athens}/members"));
        int membershipId = factory.Query(db => db.Memberships.Single(m => m.User!.Email == email).Id);
        await admin.SubmitFormAsync($"/admin/{athens}/members", $"/admin/{athens}/members/{membershipId}/approve", []);

        string after = await student.GetStringAsync("/");
        Assert.Contains("href=\"/schedule\"", after);
        Assert.Contains(">Student<", after);
    }

    [Fact]
    public async Task Joining_WithAnUnknownCode_ExplainsTheProblem()
    {
        HttpClient browser = factory.CreateBrowser();
        await browser.RegisterNewUserAsync("Kostas");

        HttpResponseMessage response = await browser.SubmitFormAsync("/join", "/join", new()
        {
            ["InviteCode"] = "NOPE2345", ["Role"] = nameof(MembershipRole.Teacher),
        });

        Assert.Contains("doesn&#x27;t match any organisation", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InviteLink_SendsNewVisitorsToRegister_AndBack()
    {
        HttpResponseMessage response = await factory.CreateBrowser(followRedirects: false).GetAsync("/join/PATRAS26");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/register?returnUrl=%2Fjoin%2FPATRAS26", response.Headers.Location!.ToString());

        HttpClient signedIn = factory.CreateBrowser();
        await signedIn.RegisterNewUserAsync("Stella");
        Assert.Contains("Join Patras Dance Studio", await signedIn.GetStringAsync("/join/PATRAS26"));
    }

    [Fact]
    public async Task AnAdmin_CannotManageAnotherOrganisation()
    {
        HttpClient patrasAdmin = factory.CreateBrowser();
        await patrasAdmin.SignInAsync("nikos@demo.secretariart.app");
        int athens = OrganizationId("Athens Drama School");

        string page = await patrasAdmin.GetStringAsync($"/admin/{athens}/members");
        Assert.Contains("have access to this page", page);

        // Even a hand-made POST (with a valid token from his own page) is refused.
        int katerina = factory.Query(db => db.Memberships.Single(m => m.User!.Email == "katerina@demo.secretariart.app").Id);
        int patras = OrganizationId("Patras Dance Studio");
        HttpResponseMessage post = await patrasAdmin.SubmitFormAsync(
            $"/admin/{patras}/members", $"/admin/{athens}/members/{katerina}/approve", []);
        Assert.Contains("have access to this page", await post.Content.ReadAsStringAsync());
        Assert.Equal(MembershipStatus.Pending, factory.Query(db => db.Memberships.Single(m => m.Id == katerina).Status));
    }

    [Fact]
    public async Task Navigation_FollowsTheUsersRolesAcrossOrganisations()
    {
        // Dimitra teaches in Athens and studies in Patras: Schedule and Payments, but no Administration.
        HttpClient dimitra = factory.CreateBrowser();
        await dimitra.SignInAsync("dimitra@demo.secretariart.app");
        string home = await dimitra.GetStringAsync("/");

        Assert.Contains("href=\"/schedule\"", home);
        Assert.Contains("href=\"/payments\"", home);
        Assert.DoesNotContain("href=\"/admin\"", home);

        string schedule = await dimitra.GetStringAsync("/schedule");
        Assert.Contains("My classes", schedule);
        Assert.Contains("Teaching", schedule);
        Assert.Contains("Availability", schedule);
    }
}
