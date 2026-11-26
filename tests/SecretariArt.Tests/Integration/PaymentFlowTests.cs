using System.Net;
using SecretariArt.Models;
using SecretariArt.Services;

namespace SecretariArt.Tests.Integration;

// Milestone 4 through the real pages: the admin's books, recording a payment, prices, a student's Payments page,
// and checking in to a per-class course (Patras' Contemporary) as a student and a teacher.
[Collection(IntegrationTests.Name)]
public class PaymentFlowTests(SecretariArtFactory factory)
{
    private const string Eleni = "eleni@demo.secretariart.app";     // admin, Athens
    private const string Nikos = "nikos@demo.secretariart.app";     // admin, Patras
    private const string Dimitra = "dimitra@demo.secretariart.app"; // teacher Athens, student Patras
    private const string Elena = "elena@demo.secretariart.app";     // teacher Patras (Contemporary)
    private const string Giorgos = "giorgos@demo.secretariart.app"; // student Athens, Year 1

    private int Athens => factory.Query(db => db.Organizations.Single(o => o.InviteCode == "ATHENS26").Id);
    private int Patras => factory.Query(db => db.Organizations.Single(o => o.InviteCode == "PATRAS26").Id);
    private static DateOnly Today => TimeProvider.System.SchoolToday();
    private static DateOnly LastMonth => Today.FirstOfMonth().AddMonths(-1);

    private async Task<HttpClient> SignedInAsync(string email)
    {
        HttpClient browser = factory.CreateBrowser();
        await browser.SignInAsync(email);
        return browser;
    }

    private static async Task<string> PageAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task TheBooks_ShowLastMonthsOverdueFee_AndOpeningThemBillsThisMonth()
    {
        HttpClient admin = await SignedInAsync(Eleni);
        string overdue = WebUtility.HtmlDecode(await admin.GetStringAsync($"/admin/{Athens}/payments?filter=Overdue"));
        Assert.Contains("Eleftheria Kanellou", overdue);
        Assert.Contains($"Year 1 – Acting (bundle), {DisplayNames.MonthName(LastMonth)}", overdue);
        Assert.Contains("Overdue since", overdue);

        // This month's bundle fee exists now, with Maria's scholarship taken off.
        string maria = factory.Query(db => db.Users.Single(u => u.Email == "maria@demo.secretariart.app").Id);
        string month = Today.FirstOfMonth().ToString("yyyy-MM");
        Assert.Equal(90m, factory.Query(db => db.Charges.Single(c => c.StudentId == maria && c.Key!.EndsWith(month)).Amount));

        HttpClient student = await SignedInAsync(Giorgos);
        string mine = WebUtility.HtmlDecode(await student.GetStringAsync("/payments"));
        Assert.Contains("Athens Drama School", mine);
        Assert.Contains($"Year 1 – Acting (bundle), {DisplayNames.MonthName(Today.FirstOfMonth())}", mine);
        Assert.Contains("Paid and waived", mine); // last month's, paid in cash
    }

    [Fact]
    public async Task AnAdmin_RecordsAPayment_AndTheStudentIsTold()
    {
        HttpClient admin = await SignedInAsync(Eleni);
        string christos = factory.Query(db => db.Users.Single(u => u.Email == "christos@demo.secretariart.app").Id);
        string page = $"/admin/{Athens}/payments/students/{christos}";
        Assert.Contains("Christos Mavros", WebUtility.HtmlDecode(await admin.GetStringAsync(page)));
        string month = Today.FirstOfMonth().ToString("yyyy-MM");
        int charge = factory.Query(db => db.Charges.Single(c => c.StudentId == christos && c.Key!.EndsWith(month)).Id);

        HttpResponseMessage tooMuch = await admin.SubmitFormAsync(page, $"/admin/{Athens}/payments/charges/{charge}/payments", new()
        {
            ["Amount"] = "500", ["PaidOn"] = Today.ToString("yyyy-MM-dd"), ["Method"] = nameof(PaymentMethod.Cash),
        });
        Assert.Contains("That's more than the €180.00 still owed", await PageAsync(tooMuch));

        HttpResponseMessage paid = await admin.SubmitFormAsync(page, $"/admin/{Athens}/payments/charges/{charge}/payments", new()
        {
            ["Amount"] = "180", ["PaidOn"] = Today.ToString("yyyy-MM-dd"), ["Method"] = nameof(PaymentMethod.Card), ["Reference"] = "POS 4411",
        });
        Assert.Contains("€180.00 from Christos Mavros recorded", await PageAsync(paid));
        Assert.Contains("ref. POS 4411", await PageAsync(paid));
        Assert.True(factory.Query(db => db.Notifications.Any(n => n.UserId == christos && n.Text.Contains("It's paid in full"))));
    }

    [Fact]
    public async Task APriceWithoutAnAmount_IsRefused_AndKeepsWhatWasTyped()
    {
        HttpClient admin = await SignedInAsync(Nikos);
        string prices = $"/admin/{Patras}/payments/prices";
        string page = WebUtility.HtmlDecode(await admin.GetStringAsync(prices));
        Assert.Contains("Per class attended", page);
        int ballet = factory.Query(db => db.Courses.Single(c => c.OrganizationId == Patras && c.Name == "Ballet for beginners").Id);

        HttpResponseMessage refused = await admin.SubmitFormAsync(prices, prices, new()
        {
            ["Courses[0].Id"] = ballet.ToString(), ["Courses[0].Pricing"] = nameof(CoursePricing.Monthly), ["Courses[0].Price"] = "",
        });
        Assert.Contains("Ballet for beginners: enter the price, or choose “No price yet”.", await PageAsync(refused));
        Assert.Equal(60m, factory.Query(db => db.Courses.Single(c => c.Id == ballet).Price));
    }

    [Fact]
    public async Task Amounts_TakeADecimalComma_AndSayWhichCourseIsWrong()
    {
        HttpClient admin = await SignedInAsync(Nikos);
        string prices = $"/admin/{Patras}/payments/prices";
        int contemporary = factory.Query(db => db.Courses.Single(c => c.OrganizationId == Patras && c.Name == "Contemporary").Id);

        HttpResponseMessage wrong = await admin.SubmitFormAsync(prices, prices, new()
        {
            ["Courses[0].Id"] = contemporary.ToString(), ["Courses[0].Pricing"] = nameof(CoursePricing.PerClass), ["Courses[0].Price"] = "1.250,00",
        });
        Assert.Contains("Contemporary: “1.250,00” isn't an amount. Write it like 45 or 12.50.", await PageAsync(wrong));
        Assert.Contains("value=\"1.250,00\"", await PageAsync(wrong)); // shown back as typed

        HttpResponseMessage unclear = await admin.SubmitFormAsync(prices, prices, new()
        {
            ["Courses[0].Id"] = contemporary.ToString(), ["Courses[0].Pricing"] = nameof(CoursePricing.PerClass), ["Courses[0].Price"] = "1.250",
        });
        Assert.Contains("“1.250” could mean 1250 or 1.25. Write whichever you mean, with no thousands separator.", await PageAsync(unclear));

        // "15,00" is €15, the price it already has, so the shared demo data stays as it was.
        HttpResponseMessage saved = await admin.SubmitFormAsync(prices, prices, new()
        {
            ["Courses[0].Id"] = contemporary.ToString(), ["Courses[0].Pricing"] = nameof(CoursePricing.PerClass), ["Courses[0].Price"] = "15,00",
        });
        Assert.Contains("Prices saved.", await PageAsync(saved));
        Assert.Equal(15m, factory.Query(db => db.Courses.Single(c => c.Id == contemporary).Price));
    }

    [Fact]
    public async Task ARefusedOneOffCharge_KeepsWhatWasTyped_AndNamesEveryMissingField()
    {
        HttpClient admin = await SignedInAsync(Eleni);
        string stavros = factory.Query(db => db.Users.Single(u => u.Email == "stavros@demo.secretariart.app").Id);
        string page = $"/admin/{Athens}/payments/students/{stavros}";

        HttpResponseMessage refused = await admin.SubmitFormAsync(page, $"/admin/{Athens}/payments/students/{stavros}/charges", new()
        {
            ["NewCharge.Description"] = "", ["NewCharge.Amount"] = "20,5", ["NewCharge.DueOn"] = "",
        });
        string html = await PageAsync(refused);
        Assert.Contains("Say what the charge is for.", html);
        Assert.Contains("Enter the due date.", html);
        Assert.Contains("value=\"20,5\"", html);
        Assert.False(factory.Query(db => db.Charges.Any(c => c.StudentId == stavros && c.Kind == ChargeKind.Other)));
    }

    [Fact]
    public async Task AfterARefusedForm_RecordingAPaymentComesBackToTheStudentsPage()
    {
        HttpClient admin = await SignedInAsync(Eleni);
        string ioanna = factory.Query(db => db.Users.Single(u => u.Email == "ioanna@demo.secretariart.app").Id);
        string page = $"/admin/{Athens}/payments/students/{ioanna}";

        HttpResponseMessage refused = await admin.SubmitFormAsync(page, $"{page}/discounts", new()
        {
            ["Kind"] = "custom", ["Percent"] = "15", ["Reason"] = "   ", ["Scope"] = "all",
        });

        string html = await PageAsync(refused);
        Assert.Contains("Choose a discount, or enter a percentage (1–100) and the reason for it.", html);
        Assert.Contains($"name=\"ReturnUrl\" value=\"{page}#charge-", html);
    }

    [Fact]
    public async Task AStudent_ChecksInToAPerClassCourse_AndIsBilledForLastMonthsClasses()
    {
        HttpClient dancer = await SignedInAsync(Dimitra);
        string tab = WebUtility.HtmlDecode(await dancer.GetStringAsync("/schedule?tab=book"));
        Assert.Contains("Check in to classes", tab);
        Assert.Contains("Contemporary", tab);
        Assert.Contains("You're checked in.", tab); // the demo check-in for next week
        Assert.Contains("Free cancellation until", tab);

        int session = factory.Query(db => db.ClassSessions.Single(s => s.OrganizationId == Patras && s.Group!.Course!.Name == "Contemporary").Id);
        DateOnly booked = factory.Query(db => db.Bookings.Where(b => b.SessionId == session && b.Date > Today).Min(b => b.Date));
        HttpResponseMessage response = await dancer.SubmitFormAsync("/schedule?tab=book", "/schedule/book", new()
        {
            ["sessionId"] = session.ToString(), ["date"] = booked.AddDays(7).ToString("yyyy-MM-dd"),
        });
        Assert.Contains("You're checked in to Contemporary", await PageAsync(response));

        // Last month: one class attended, one missed without cancelling. Patras bills no-shows: both count.
        string payments = WebUtility.HtmlDecode(await dancer.GetStringAsync("/payments"));
        Assert.Contains($"Contemporary, {DisplayNames.MonthName(LastMonth)}: 2 classes × €15.00", payments);
        Assert.Contains("Patras Dance Studio", payments);

        HttpClient teacher = await SignedInAsync(Elena);
        string coming = WebUtility.HtmlDecode(await teacher.GetStringAsync("/schedule?tab=check-ins"));
        Assert.Contains("Who's coming", coming);
        Assert.Contains("of 12 checked in", coming);
    }

    [Fact]
    public async Task AnAdmin_SeesSettingsCheckInsAndAClassesList()
    {
        HttpClient admin = await SignedInAsync(Nikos);
        string settings = WebUtility.HtmlDecode(await admin.GetStringAsync($"/admin/{Patras}/payments/settings"));
        Assert.Contains("Bill students who check in but don't come", settings);
        Assert.Contains("checked=\"checked\"", settings);

        string checkIns = WebUtility.HtmlDecode(await admin.GetStringAsync($"/admin/{Patras}/payments/check-ins"));
        Assert.Contains("Contemporary", checkIns);

        int session = factory.Query(db => db.ClassSessions.Single(s => s.OrganizationId == Patras && s.Group!.Course!.Name == "Contemporary").Id);
        DateOnly booked = factory.Query(db => db.Bookings.Where(b => b.SessionId == session && b.Date > Today).Min(b => b.Date));
        string roster = WebUtility.HtmlDecode(await admin.GetStringAsync($"/admin/{Patras}/payments/check-ins/{session}/{booked:yyyy-MM-dd}"));
        Assert.Contains("Dimitra", roster);
        Assert.Contains("Once the class has started", roster);

        HttpClient teacher = await SignedInAsync(Elena);
        Assert.Contains("Dimitra", WebUtility.HtmlDecode(await teacher.GetStringAsync($"/schedule/check-ins/{Patras}/{session}/{booked:yyyy-MM-dd}")));
        Assert.Equal(HttpStatusCode.NotFound, (await teacher.GetAsync($"/schedule/check-ins/{Athens}/{session}/{booked:yyyy-MM-dd}")).StatusCode);
    }

    [Fact]
    public async Task TheBooks_AreForAdminsOfThatOrganisationOnly()
    {
        HttpClient student = await SignedInAsync(Giorgos);
        HttpResponseMessage response = await student.GetAsync($"/admin/{Athens}/payments");
        Assert.Contains("/account/access-denied", response.RequestMessage!.RequestUri!.ToString());

        HttpClient otherAdmin = await SignedInAsync(Nikos);
        Assert.Contains("/account/access-denied",
            (await otherAdmin.GetAsync($"/admin/{Athens}/payments/settings")).RequestMessage!.RequestUri!.ToString());
    }
}
