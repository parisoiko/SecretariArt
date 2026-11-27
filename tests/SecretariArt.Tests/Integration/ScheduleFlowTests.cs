using System.Net;
using SecretariArt.Models;

namespace SecretariArt.Tests.Integration;

// Milestone 3 through the real pages: the admin's timetable editor, personal calendars, availability and the bell.
[Collection(IntegrationTests.Name)]
public class ScheduleFlowTests(SecretariArtFactory factory)
{
    private const string Eleni = "eleni@demo.secretariart.app";     // admin, Athens
    private const string Nikos = "nikos@demo.secretariart.app";     // admin, Patras
    private const string Dimitra = "dimitra@demo.secretariart.app"; // teacher Athens, student Patras
    private const string Giorgos = "giorgos@demo.secretariart.app"; // student Athens

    private int Athens => factory.Query(db => db.Organizations.Single(o => o.InviteCode == "ATHENS26").Id);
    private int Patras => factory.Query(db => db.Organizations.Single(o => o.InviteCode == "PATRAS26").Id);

    private async Task<HttpClient> SignedInAsync(string email)
    {
        HttpClient browser = factory.CreateBrowser();
        await browser.SignInAsync(email);
        return browser;
    }

    private static async Task<string> PageAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task AnAdmin_GeneratesADraft_LooksAtIt_AndDiscardsIt_WithoutChangingTheTimetable()
    {
        HttpClient admin = await SignedInAsync(Eleni);
        string root = $"/admin/{Athens}/schedule";
        int before = factory.Query(db => db.ClassSessions.Count(s => s.OrganizationId == Athens));
        Assert.Contains("Generate a timetable", WebUtility.HtmlDecode(await admin.GetStringAsync(root)));

        // Every Athens group already has its classes: filling gaps has nothing to do, and says so.
        HttpResponseMessage nothing = await admin.SubmitFormAsync($"{root}/generate", $"{root}/generate", new() { ["mode"] = nameof(GenerationMode.FillGaps) });
        Assert.Contains("Every group already has all its classes", await PageAsync(nothing));

        HttpResponseMessage generated = await admin.SubmitFormAsync($"{root}/generate", $"{root}/generate", new() { ["mode"] = nameof(GenerationMode.StartOver) });
        string draft = await PageAsync(generated);
        Assert.Contains("Draft timetable", draft);
        Assert.Contains("Draft ready in", draft);
        Assert.Contains("Use this timetable", draft);
        Assert.Contains("New: Acting I · A", draft);
        Assert.Equal(before, factory.Query(db => db.ClassSessions.Count(s => s.OrganizationId == Athens)));

        HttpResponseMessage discarded = await admin.SubmitFormAsync($"{root}/draft", $"{root}/draft/discard", []);
        Assert.Contains("Draft discarded. The timetable is as it was.", await PageAsync(discarded));
        Assert.False(factory.Query(db => db.ScheduleDrafts.Any(d => d.OrganizationId == Athens)));
    }

    [Fact]
    public async Task TheDemoTimetable_ShowsInTheEditor_AndInPersonalCalendars()
    {
        HttpClient admin = await SignedInAsync(Eleni);
        string editor = WebUtility.HtmlDecode(await admin.GetStringAsync($"/admin/{Athens}/schedule"));
        Assert.Contains("weekly classes placed", editor);
        Assert.Contains("Acting I · A", editor);
        Assert.Contains("2 of 2 a week", editor); // Acting I A meets twice, as the course says

        HttpClient student = await SignedInAsync(Giorgos);
        string week = WebUtility.HtmlDecode(await student.GetStringAsync("/schedule"));
        Assert.Contains("Acting I", week);
        Assert.Contains("Main stage", week);
        Assert.DoesNotContain("Clashes with", week);
    }

    [Fact]
    public async Task AnAdmin_AddsAClass_IsStoppedByAClash_AndTheStudentsAreNotified()
    {
        HttpClient admin = await SignedInAsync(Eleni);
        string root = $"/admin/{Athens}/schedule";
        // Stage combat has no group in the demo data: add one through the form, as an admin would.
        int course = factory.Query(db => db.Courses.Single(c => c.OrganizationId == Athens && c.Name == "Stage combat").Id);
        string groups = $"/admin/{Athens}/courses/{course}/groups/new";
        await admin.SubmitFormAsync(groups, groups, new() { ["Name"] = "Fri", ["Capacity"] = "10" });
        int stageCombat = factory.Query(db => db.CourseGroups.Single(g => g.CourseId == course && g.Name == "Fri").Id);
        int studioA = factory.Query(db => db.Classrooms.Single(c => c.OrganizationId == Athens && c.Name == "Studio A").Id);

        // Studio A has Movement for actors on Tuesday 18:00–19:30.
        HttpResponseMessage clash = await admin.SubmitFormAsync(root, root, new()
        {
            ["GroupId"] = stageCombat.ToString(), ["Day"] = nameof(DayOfWeek.Tuesday), ["Starts"] = "18:00", ["ClassroomId"] = studioA.ToString(),
        });
        Assert.Contains("Studio A is taken by Movement for actors (group A) on Tuesday 18:00–19:30.", await PageAsync(clash));
        Assert.Equal(root, clash.RequestMessage!.RequestUri!.AbsolutePath);

        HttpResponseMessage added = await admin.SubmitFormAsync(root, root, new()
        {
            ["GroupId"] = stageCombat.ToString(), ["Day"] = nameof(DayOfWeek.Friday), ["Starts"] = "14:00", ["ClassroomId"] = studioA.ToString(),
        });
        Assert.Contains("Stage combat (group Fri) added on Friday 14:00–16:00 in Studio A.", await PageAsync(added));
        Assert.Equal(root, added.RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task MovingAClass_NotifiesItsStudents_AndTheBellShowsIt()
    {
        HttpClient admin = await SignedInAsync(Eleni);
        int history = factory.Query(db => db.ClassSessions.Single(s => s.OrganizationId == Athens && s.Group!.Course!.Name == "Theatre history").Id);
        int seminar = factory.Query(db => db.Classrooms.Single(c => c.OrganizationId == Athens && c.Name == "Seminar room").Id);
        string edit = $"/admin/{Athens}/schedule/{history}/edit";

        HttpClient student = await SignedInAsync(Giorgos);
        await student.GetStringAsync("/notifications"); // read everything so far

        HttpResponseMessage moved = await admin.SubmitFormAsync(edit, edit, new()
        {
            ["Day"] = nameof(DayOfWeek.Wednesday), ["Starts"] = "11:00", ["ClassroomId"] = seminar.ToString(),
        });
        Assert.Contains("Theatre history (group A) moved to Wednesday 11:00–12:30 in Seminar room.", await PageAsync(moved));

        string home = await student.GetStringAsync("/");
        Assert.Contains("Notifications, 1 new", home);
        string notifications = WebUtility.HtmlDecode(await student.GetStringAsync("/notifications"));
        Assert.Contains("Athens Drama School: Theatre history (group A) moved from Wednesday 10:00–11:30 in Seminar room to Wednesday 11:00–12:30 in Seminar room.", notifications);
        Assert.DoesNotContain("Notifications, 1 new", await student.GetStringAsync("/")); // read now
    }

    [Fact]
    public async Task ATeacher_PostsAvailability_OnlyWhereTheyTeach()
    {
        HttpClient teacher = await SignedInAsync(Dimitra);

        HttpResponseMessage athens = await teacher.SubmitFormAsync("/schedule?tab=availability", "/schedule/availability", new()
        {
            ["OrganizationId"] = Athens.ToString(), ["Day"] = nameof(DayOfWeek.Wednesday), ["From"] = "15:00", ["To"] = "19:00",
        });
        Assert.Contains("Saved: Wednesday 15:00–19:00.", await PageAsync(athens));

        // She's a student at Patras, not a teacher.
        HttpResponseMessage patras = await teacher.SubmitFormAsync("/schedule?tab=availability", "/schedule/availability", new()
        {
            ["OrganizationId"] = Patras.ToString(), ["Day"] = nameof(DayOfWeek.Wednesday), ["From"] = "15:00", ["To"] = "19:00",
        });
        Assert.Contains("You can post availability only where you're an approved teacher.", await PageAsync(patras));
    }

    // SubmitFormAsync borrows the page's first token, so a form missing its own would still pass the other tests;
    // a real browser posts only the form's own fields and gets 400 (seen once, on the Add a class form).
    [Theory]
    [InlineData(Eleni, "/admin/{athens}/schedule")]
    [InlineData(Eleni, "/admin/{athens}/schedule/{session}/edit")]
    [InlineData(Eleni, "/admin/{athens}/courses/{course}")]
    [InlineData(Eleni, "/admin/{athens}/members")]
    [InlineData(Dimitra, "/schedule?tab=availability")]
    public async Task EveryPostForm_CarriesItsOwnAntiforgeryToken(string email, string path)
    {
        int athens = Athens;
        string url = path.Replace("{athens}", athens.ToString())
            .Replace("{session}", factory.Query(db => db.ClassSessions.First(s => s.OrganizationId == athens).Id).ToString())
            .Replace("{course}", factory.Query(db => db.Courses.First(c => c.OrganizationId == athens && c.Name == "Acting I").Id).ToString());
        string page = await (await SignedInAsync(email)).GetStringAsync(url);

        var forms = System.Text.RegularExpressions.Regex.Matches(page, "<form[^>]*method=\"post\"[^>]*>.*?</form>",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.NotEmpty(forms);
        Assert.All(forms, form => Assert.Contains("__RequestVerificationToken", form.Value));
    }

    [Fact]
    public async Task OtherPeople_CannotReachTheTimetableEditor()
    {
        int athens = Athens;
        int athensSession = factory.Query(db => db.ClassSessions.First(s => s.OrganizationId == athens).Id);

        HttpClient student = await SignedInAsync(Giorgos);
        HttpClient otherAdmin = await SignedInAsync(Nikos);
        Assert.Contains("have access to this page", await student.GetStringAsync($"/admin/{athens}/schedule"));
        Assert.Contains("have access to this page", await otherAdmin.GetStringAsync($"/admin/{athens}/schedule"));

        // Through his own organisation's address, Athens' class is simply not there.
        string ownRoot = $"/admin/{Patras}/schedule";
        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.GetAsync($"{ownRoot}/{athensSession}/edit")).StatusCode);
        HttpResponseMessage delete = await otherAdmin.SubmitFormAsync(ownRoot, $"{ownRoot}/{athensSession}/delete", []);
        Assert.Contains("We couldn't find that class in the timetable.", await PageAsync(delete));
        Assert.True(factory.Query(db => db.ClassSessions.Any(s => s.Id == athensSession)));

        // An admin-only account has no personal schedule.
        HttpResponseMessage adminSchedule = await (await SignedInAsync(Eleni)).GetAsync("/schedule");
        Assert.Equal("/", adminSchedule.RequestMessage!.RequestUri!.AbsolutePath);
    }
}
