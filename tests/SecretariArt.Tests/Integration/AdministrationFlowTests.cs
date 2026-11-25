using System.Net;
using SecretariArt.Models;

namespace SecretariArt.Tests.Integration;

// Milestone 2 through the real pages: an admin sets rules, adds a classroom, a course with a group and a bundle, and
// enrols a student; other people can't reach any of it.
[Collection(IntegrationTests.Name)]
public class AdministrationFlowTests(SecretariArtFactory factory)
{
    private const string Eleni = "eleni@demo.secretariart.app";   // admin, Athens
    private const string Nikos = "nikos@demo.secretariart.app";   // admin, Patras
    private const string Giorgos = "giorgos@demo.secretariart.app"; // student, Athens

    private int Athens => factory.Query(db => db.Organizations.Single(o => o.InviteCode == "ATHENS26").Id);
    private int Patras => factory.Query(db => db.Organizations.Single(o => o.InviteCode == "PATRAS26").Id);

    private static void AssertLandedOn(string path, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(path, response.RequestMessage!.RequestUri!.AbsolutePath, ignoreCase: true);
    }

    private async Task<HttpClient> SignedInAsync(string email)
    {
        HttpClient browser = factory.CreateBrowser();
        await browser.SignInAsync(email);
        return browser;
    }

    [Fact]
    public async Task DemoData_FillsEveryTab()
    {
        HttpClient admin = await SignedInAsync(Eleni);
        string root = $"/admin/{Athens}";

        Assert.Contains("Last saved", await admin.GetStringAsync($"{root}/rules"));
        Assert.Contains("Main stage", await admin.GetStringAsync($"{root}/classrooms"));
        string courses = await admin.GetStringAsync($"{root}/courses");
        Assert.Contains("Acting I", courses);
        Assert.Contains("No teacher is assigned to it yet.", courses); // Stage combat
        Assert.Contains("Year 1 – Acting", WebUtility.HtmlDecode(await admin.GetStringAsync($"{root}/bundles")));
        Assert.Contains("Giorgos Alexiou", await admin.GetStringAsync($"{root}/enrolments"));
        // Athens has done every step, so the checklist is gone; Patras has no rules yet, so it shows.
        Assert.DoesNotContain("Getting started", await admin.GetStringAsync($"{root}/members"));

        HttpClient nikos = await SignedInAsync(Nikos);
        Assert.Contains("Getting started", await nikos.GetStringAsync($"/admin/{Patras}/members"));
    }

    [Fact]
    public async Task AnAdmin_SetsUpACourseAndEnrolsAStudent_ThroughTheForms()
    {
        HttpClient admin = await SignedInAsync(Eleni);
        string root = $"/admin/{Athens}";
        string suffix = Guid.NewGuid().ToString("N")[..6];

        HttpResponseMessage room = await admin.SubmitFormAsync($"{root}/classrooms/new", $"{root}/classrooms/new", new()
        {
            ["Name"] = $"Piano room {suffix}", ["Capacity"] = "6", ["Floor"] = nameof(FloorType.Carpet), ["Features"] = nameof(RoomFeatures.Piano),
        });
        Assert.Contains($"Piano room {suffix} added.", await room.Content.ReadAsStringAsync());
        // The message alone isn't proof: the error page shows it too. Check where the redirect landed.
        AssertLandedOn($"{root}/classrooms", room);

        string courseName = $"Singing {suffix}";
        HttpResponseMessage created = await admin.SubmitFormAsync($"{root}/courses/new", $"{root}/courses/new", new()
        {
            ["Name"] = courseName, ["SessionMinutes"] = "60", ["SessionsPerWeek"] = "1", ["RequiredFeatures"] = nameof(RoomFeatures.Piano),
        });
        string coursePage = await created.Content.ReadAsStringAsync();
        Assert.Contains($"{courseName} added. Add a group to it", coursePage); // lands on the new course's page
        int courseId = factory.Query(db => db.Courses.Single(c => c.Name == courseName).Id);
        AssertLandedOn($"{root}/courses/{courseId}", created);

        HttpResponseMessage group = await admin.SubmitFormAsync($"{root}/courses/{courseId}/groups/new", $"{root}/courses/{courseId}/groups/new",
            new() { ["Name"] = "A", ["Capacity"] = "6" });
        AssertLandedOn($"{root}/courses/{courseId}", group);
        int groupId = factory.Query(db => db.CourseGroups.Single(g => g.CourseId == courseId).Id);

        string giorgos = factory.Query(db => db.Users.Single(u => u.Email == Giorgos).Id);
        HttpResponseMessage enrolled = await admin.SubmitFormAsync($"{root}/enrolments/students/{giorgos}",
            $"{root}/enrolments/students/{giorgos}/group", new() { ["GroupId"] = groupId.ToString() });
        Assert.Contains($"Giorgos Alexiou enrolled in {courseName}, group A.", await enrolled.Content.ReadAsStringAsync());
        AssertLandedOn($"{root}/enrolments/students/{giorgos}", enrolled);

        HttpResponseMessage rules = await admin.SubmitFormAsync($"{root}/rules", $"{root}/rules", new()
        {
            ["Days"] = "Monday", ["DayStarts"] = "10:00", ["DayEnds"] = "22:00", ["SlotMinutes"] = "15",
            ["MinSessionMinutes"] = "45", ["MaxSessionMinutes"] = "180", ["MinSessionsPerWeek"] = "1", ["MaxSessionsPerWeek"] = "3",
            ["BreakBetweenSessionsMinutes"] = "15",
        });
        AssertLandedOn($"{root}/rules", rules);

        // A 6-person group fits the 6-person piano room, so no warning for it.
        string details = await admin.GetStringAsync($"{root}/courses/{courseId}");
        Assert.Contains("1 of 6 places taken", details);
        Assert.DoesNotContain("largest suitable room", details);
    }

    [Fact]
    public async Task InvalidForms_ExplainTheProblem_AndKeepWhatWasTyped()
    {
        HttpClient admin = await SignedInAsync(Eleni);
        string root = $"/admin/{Athens}";

        HttpResponseMessage rules = await admin.SubmitFormAsync($"{root}/rules", $"{root}/rules", new()
        {
            ["Days"] = "Monday", ["DayStarts"] = "18:00", ["DayEnds"] = "09:00", ["SlotMinutes"] = "15",
            ["MinSessionMinutes"] = "45", ["MaxSessionMinutes"] = "180", ["MinSessionsPerWeek"] = "1", ["MaxSessionsPerWeek"] = "3",
            ["BreakBetweenSessionsMinutes"] = "0",
        });
        Assert.Contains("The day must end after it starts.", await rules.Content.ReadAsStringAsync());

        HttpResponseMessage course = await admin.SubmitFormAsync($"{root}/courses/new", $"{root}/courses/new", new()
        {
            ["Name"] = "Marathon rehearsal", ["SessionMinutes"] = "600", ["SessionsPerWeek"] = "1",
        });
        string page = await course.Content.ReadAsStringAsync();
        Assert.Contains("Sessions must last between 45 min and 3 h", page);
        Assert.Contains("value=\"Marathon rehearsal\"", page);
    }

    [Fact]
    public async Task OtherPeople_CannotReachTheAdministrationTabs()
    {
        int athens = Athens;
        HttpClient student = await SignedInAsync(Giorgos);
        HttpClient otherAdmin = await SignedInAsync(Nikos);

        foreach (string tab in new[] { "rules", "classrooms", "courses", "bundles", "enrolments" })
        {
            Assert.Contains("have access to this page", await student.GetStringAsync($"/admin/{athens}/{tab}"));
            Assert.Contains("have access to this page", await otherAdmin.GetStringAsync($"/admin/{athens}/{tab}"));
        }

        HttpResponseMessage signedOut = await factory.CreateBrowser(followRedirects: false).GetAsync($"/admin/{athens}/courses");
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Contains("/account/login", signedOut.Headers.Location!.ToString());
    }

    [Fact]
    public async Task AnAdmin_CannotReachAnotherOrganisationsItems_ThroughTheirOwnRoutes()
    {
        HttpClient nikos = await SignedInAsync(Nikos);
        string ownRoot = $"/admin/{Patras}";
        int athensRoom = factory.Query(db => db.Classrooms.First(c => c.OrganizationId == Athens).Id);
        int athensCourse = factory.Query(db => db.Courses.First(c => c.OrganizationId == Athens).Id);

        Assert.Equal(HttpStatusCode.NotFound, (await nikos.GetAsync($"{ownRoot}/classrooms/{athensRoom}/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await nikos.GetAsync($"{ownRoot}/courses/{athensCourse}")).StatusCode);

        HttpResponseMessage delete = await nikos.SubmitFormAsync($"{ownRoot}/classrooms", $"{ownRoot}/classrooms/{athensRoom}/delete", []);
        Assert.Contains("We couldn&#x27;t find that classroom.", await delete.Content.ReadAsStringAsync());
        AssertLandedOn($"{ownRoot}/classrooms", delete);
        Assert.True(factory.Query(db => db.Classrooms.Any(c => c.Id == athensRoom)));

        string athensStudent = factory.Query(db => db.Users.Single(u => u.Email == Giorgos).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await nikos.GetAsync($"{ownRoot}/enrolments/students/{athensStudent}")).StatusCode);
    }
}
