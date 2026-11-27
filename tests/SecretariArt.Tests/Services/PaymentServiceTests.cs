using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;
using SecretariArt.Services;

namespace SecretariArt.Tests.Services;

// Milestone 4: prices, discounts, charges made by billing, payments, reminders, and checking in to per-class courses.
// The clock starts on Tuesday 1 September 2026, 12:00 in Athens. One organisation: "Acting" by the month (€100),
// "Life drawing" per class (€15, Wednesdays 19:00, 3 places), and a "Year 1" bundle.
public sealed class PaymentServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SecretariArtDbContext _db;
    private readonly MovableClock _clock = new();
    private readonly BillingService _billing;
    private readonly PaymentSetupService _setup;
    private readonly BookingService _bookings;
    private readonly EnrolmentService _enrolments;
    private readonly ScheduleService _schedule;
    private readonly CourseService _courses;

    private readonly int _org;
    private readonly ApplicationUser _admin;
    private readonly ApplicationUser _student;
    private readonly Course _acting;
    private readonly Course _drawing;
    private readonly CourseGroup _actingA;
    private readonly CourseGroup _drawingA;
    private readonly ClassSession _drawingClass;
    private static readonly DateOnly Wednesday = new(2026, 9, 2);

    public PaymentServiceTests()
    {
        _connection.Open();
        _db = new SecretariArtDbContext(new DbContextOptionsBuilder<SecretariArtDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var memberships = new MembershipService(_db, _clock);
        var notifications = new NotificationService(_db, _clock);
        _billing = new BillingService(_db, notifications, _clock);
        _setup = new PaymentSetupService(_db, memberships, _billing, _clock);
        _bookings = new BookingService(_db, _clock);
        _enrolments = new EnrolmentService(_db, memberships, _clock, notifications);
        var rules = new RulesService(_db, _clock);
        _schedule = new ScheduleService(_db, rules, notifications, _clock);
        _courses = new CourseService(_db, rules, memberships, notifications, _clock);

        _admin = AddUser("admin");
        var organization = new Organization
        {
            Name = "Atelier", City = "Athens", ContactEmail = "x@test.example", Status = OrganizationStatus.Active,
            InviteCode = "ATELIER1", RequestedById = _admin.Id,
        };
        _db.Organizations.Add(organization);
        _db.SaveChanges();
        _org = organization.Id;
        _student = AddStudent("student");

        var room = new Classroom { OrganizationId = _org, Name = "Studio", Capacity = 20 };
        _acting = new Course { OrganizationId = _org, Name = "Acting", SessionMinutes = 120, SessionsPerWeek = 1, Pricing = CoursePricing.Monthly, Price = 100 };
        _drawing = new Course { OrganizationId = _org, Name = "Life drawing", SessionMinutes = 120, SessionsPerWeek = 1, Pricing = CoursePricing.PerClass, Price = 15 };
        _actingA = new CourseGroup { OrganizationId = _org, Course = _acting, Name = "A", Capacity = 10 };
        _drawingA = new CourseGroup { OrganizationId = _org, Course = _drawing, Name = "A", Capacity = 3 };
        _drawingClass = new ClassSession
        {
            OrganizationId = _org, Group = _drawingA, Classroom = room, Day = DayOfWeek.Wednesday, Starts = new TimeOnly(19, 0), Ends = new TimeOnly(21, 0),
        };
        _db.AddRange(room, _acting, _drawing, _actingA, _drawingA, _drawingClass);
        _db.SaveChanges();
        Enrol(_student, _actingA);
        Enrol(_student, _drawingA);
    }

    // ---- Setup helpers ----

    private ApplicationUser AddUser(string name)
    {
        var user = new ApplicationUser { UserName = $"{name}@test.example", Email = $"{name}@test.example", FirstName = name, LastName = "Test" };
        _db.Users.Add(user);
        _db.SaveChanges();
        return user;
    }

    private ApplicationUser AddStudent(string name)
    {
        ApplicationUser user = AddUser(name);
        _db.Memberships.Add(new Membership { UserId = user.Id, OrganizationId = _org, Role = MembershipRole.Student, Status = MembershipStatus.Active });
        _db.SaveChanges();
        return user;
    }

    private void Enrol(ApplicationUser student, CourseGroup group, Bundle? bundle = null)
    {
        _db.Enrolments.Add(new Enrolment { OrganizationId = _org, StudentId = student.Id, CourseId = group.CourseId, GroupId = group.Id, BundleId = bundle?.Id });
        _db.SaveChanges();
    }

    private void Settings(bool billNoShows, int cancelHours = 4, int dueDay = 5, int reminderDays = 3) =>
        Assert.True(_setup.SaveSettingsAsync(_org, new PaymentSettingsInput(dueDay, reminderDays, billNoShows, cancelHours)).Result.Succeeded);

    // Moves the clock to a school (Athens) time; summer time is UTC+3 until late October.
    private void SetSchoolTime(int month, int day, int hour, int minute = 0)
    {
        var target = new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromHours(month is >= 4 and <= 10 ? 3 : 2));
        _clock.Advance(target - _clock.GetUtcNow());
    }

    private List<Charge> Charges(ApplicationUser? student = null) =>
        _db.Charges.AsNoTracking().Include(c => c.Payments).Where(c => c.StudentId == (student ?? _student).Id).OrderBy(c => c.Id).ToList();

    // ---- Monthly fees and discounts ----

    [Fact]
    public async Task MonthlyFees_AreMadeOncePerMonth_WithDiscountsTakenInTurn()
    {
        Assert.True((await _setup.AddDiscountAsync(_org, new DiscountInput("Siblings", 25))).Succeeded);
        int siblings = _db.Discounts.Single().Id;
        Assert.True((await _setup.GiveDiscountAsync(_org, _student.Id, new StudentDiscountInput(siblings, null, null, null, null))).Succeeded);
        Assert.True((await _setup.GiveDiscountAsync(_org, _student.Id, new StudentDiscountInput(null, 10, "Early enrolment", _acting.Id, null))).Succeeded);

        await _billing.EnsureChargesAsync(_org);
        await _billing.EnsureChargesAsync(_org);

        // 25% then 10% off €100: €75, then €67.50. Per-class courses aren't billed by the month.
        Charge fee = Assert.Single(Charges());
        Assert.Equal("Acting, September 2026", fee.Description);
        Assert.Equal(100m, fee.BaseAmount);
        Assert.Equal(67.50m, fee.Amount);
        Assert.Equal("Siblings 25%, Early enrolment 10%", fee.DiscountNote);
        Assert.Equal(new DateOnly(2026, 9, 5), fee.DueOn);

        SetSchoolTime(10, 1, 9);
        await _billing.EnsureChargesAsync(_org);
        Assert.Equal(["Acting, September 2026", "Acting, October 2026"], Charges().Select(c => c.Description));
    }

    [Fact]
    public async Task AFeeMadeAfterTheDueDay_GivesAWeekToPay_AndMonthsOutsideTheTermAreNotBilled()
    {
        SetSchoolTime(9, 20, 10);
        await _billing.EnsureChargesAsync(_org);
        Assert.Equal(new DateOnly(2026, 9, 27), Assert.Single(Charges()).DueOn);

        _db.OrganizationRules.Add(new OrganizationRules { OrganizationId = _org, TermStarts = new DateOnly(2026, 9, 14), TermEnds = new DateOnly(2026, 9, 30) });
        _db.SaveChanges();
        SetSchoolTime(10, 2, 10);
        await _billing.EnsureChargesAsync(_org);
        Assert.Single(Charges());
    }

    [Fact]
    public async Task ABundlePrice_ReplacesItsCoursesPrices()
    {
        var bundle = new Bundle { OrganizationId = _org, Name = "Year 1", MonthlyPrice = 150, Courses = [new BundleCourse { CourseId = _acting.Id }] };
        _db.Bundles.Add(bundle);
        _db.SaveChanges();
        ApplicationUser other = AddStudent("bundled");
        Enrol(other, _actingA, bundle);

        await _billing.EnsureChargesAsync(_org);

        Charge fee = Assert.Single(Charges(other));
        Assert.Equal(ChargeKind.Bundle, fee.Kind);
        Assert.Equal("Year 1 (bundle), September 2026", fee.Description);
        Assert.Equal(150m, fee.Amount);
    }

    [Fact]
    public async Task ABundlePriceSetLater_ReplacesTheCourseFeesAlreadyMade_AndClearingItBringsThemBack()
    {
        var bundle = new Bundle { OrganizationId = _org, Name = "Year 1", Courses = [new BundleCourse { CourseId = _acting.Id }] };
        _db.Bundles.Add(bundle);
        _db.SaveChanges();
        ApplicationUser other = AddStudent("bundled");
        Enrol(other, _actingA, bundle);
        await _billing.EnsureChargesAsync(_org);
        Assert.Equal(ChargeKind.Monthly, Charges(other).Single().Kind);

        ServiceResult priced = await _setup.SavePricesAsync(_org, [], [new BundlePriceInput(bundle.Id, 150)]);
        Assert.Contains("brought up to date: 1 removed, 1 added", priced.Message);
        Charge fee = Charges(other).Single();
        Assert.Equal((ChargeKind.Bundle, 150m), (fee.Kind, fee.Amount));

        await _setup.SavePricesAsync(_org, [], [new BundlePriceInput(bundle.Id, null)]);
        Assert.Equal((ChargeKind.Monthly, 100m), (Charges(other).Single().Kind, Charges(other).Single().Amount));
    }

    [Fact]
    public async Task ABundlePriceSetLater_WaitsUntilNextMonth_ForAStudentWhoAlreadyPaidACourseFee()
    {
        var bundle = new Bundle { OrganizationId = _org, Name = "Year 1", Courses = [new BundleCourse { CourseId = _acting.Id }] };
        _db.Bundles.Add(bundle);
        _db.SaveChanges();
        ApplicationUser payer = AddStudent("payer");
        Enrol(payer, _actingA, bundle);
        await _billing.EnsureChargesAsync(_org);
        Charge fee = Charges(payer).Single();
        await _billing.RecordPaymentAsync(_org, fee.Id, new PaymentInput(40, new DateOnly(2026, 9, 1), PaymentMethod.Cash, null), _admin.Id);

        ServiceResult priced = await _setup.SavePricesAsync(_org, [], [new BundlePriceInput(bundle.Id, 150)]);

        Assert.Contains("payer Test already paid part of this month's course fees, so their bundle price starts next month.", priced.Message);
        Assert.Equal(["Acting, September 2026"], Charges(payer).Select(c => c.Description));
        SetSchoolTime(10, 1, 9);
        await _billing.EnsureChargesAsync(_org);
        Assert.Equal(["Acting, September 2026", "Year 1 (bundle), October 2026"], Charges(payer).Select(c => c.Description));
    }

    [Fact]
    public async Task RemovingAPayment_TellsTheStudent()
    {
        await _billing.EnsureChargesAsync(_org);
        Charge fee = Charges().Single();
        await _billing.RecordPaymentAsync(_org, fee.Id, new PaymentInput(100, new DateOnly(2026, 9, 1), PaymentMethod.Cash, null), _admin.Id);

        await _billing.DeletePaymentAsync(_org, _db.Payments.Single().Id);

        Assert.Contains(_db.Notifications.Where(n => n.UserId == _student.Id), n =>
            n.Text == "Atelier: The payment of €100.00 on Tue 1 Sept for Acting, September 2026 was removed from your account, so it's owed again. Ask the office if you think that's a mistake.");
    }

    [Fact]
    public async Task AStudentsPerClassPrice_IncludesTheirDiscount()
    {
        Settings(billNoShows: true, cancelHours: 4);
        await _setup.AddDiscountAsync(_org, new DiscountInput("Staff family", 20));
        await _setup.GiveDiscountAsync(_org, _student.Id, new StudentDiscountInput(_db.Discounts.Single().Id, null, null, null, null));
        await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday);

        Assert.Equal(12m, (await _bookings.GetUpcomingForStudentAsync(_student.Id)).First().Price);
        SetSchoolTime(9, 2, 17);
        Assert.Contains("so it will still be billed (€12.00)", (await _bookings.CancelAsync(_student.Id, _db.Bookings.Single().Id)).Message);
    }

    [Fact]
    public async Task AStudentTakingPartOfABundle_PaysTheirCoursesOwnPrices()
    {
        var bundle = new Bundle
        {
            OrganizationId = _org, Name = "Year 1", MonthlyPrice = 150,
            Courses = [new BundleCourse { CourseId = _acting.Id }, new BundleCourse { CourseId = _drawing.Id }],
        };
        _db.Bundles.Add(bundle);
        _db.SaveChanges();
        ApplicationUser partial = AddStudent("partial");
        Enrol(partial, _actingA, bundle);

        await _billing.EnsureChargesAsync(_org);

        Charge fee = Charges(partial).Single();
        Assert.Equal(("Acting, September 2026", 100m), (fee.Description, fee.Amount));
    }

    [Fact]
    public async Task Prices_AreCheckedAllAtOnce_InWordsThatFitTheValue()
    {
        var bundle = new Bundle { OrganizationId = _org, Name = "Year 1" };
        _db.Bundles.Add(bundle);
        _db.SaveChanges();

        ServiceResult refused = await _setup.SavePricesAsync(_org,
            [new CoursePriceInput(_acting.Id, CoursePricing.Monthly, -5), new CoursePriceInput(_drawing.Id, CoursePricing.PerClass, 0.001m)],
            [new BundlePriceInput(bundle.Id, 0)]);

        Assert.Equal(
            "Acting: a price can't be negative.\nLife drawing: use whole cents, e.g. 12.50.\n" +
            "Year 1 (bundle): enter a price above €0, or leave it empty to use its courses' prices.", refused.Error);
        Assert.Equal(100m, _db.Courses.AsNoTracking().Single(c => c.Id == _acting.Id).Price);
    }

    [Fact]
    public async Task TheSameNamedDiscount_CantBeGivenTwiceOnTheSameThing()
    {
        await _setup.AddDiscountAsync(_org, new DiscountInput("Siblings", 25));
        int siblings = _db.Discounts.Single().Id;
        var input = new StudentDiscountInput(siblings, null, null, null, null);
        Assert.True((await _setup.GiveDiscountAsync(_org, _student.Id, input)).Succeeded);

        Assert.Equal("student already has Siblings on everything.", (await _setup.GiveDiscountAsync(_org, _student.Id, input)).Error);
        Assert.True((await _setup.GiveDiscountAsync(_org, _student.Id, input with { CourseId = _acting.Id })).Succeeded);
    }

    [Fact]
    public async Task NewPrices_UpdateThisMonthsUnpaidFees_ButNotPaidOnes()
    {
        ApplicationUser payer = AddStudent("payer");
        Enrol(payer, _actingA);
        await _billing.EnsureChargesAsync(_org);
        Charge paid = Charges(payer).Single();
        Assert.True((await _billing.RecordPaymentAsync(_org, paid.Id, new PaymentInput(100, new DateOnly(2026, 9, 1), PaymentMethod.Cash, null), _admin.Id)).Succeeded);

        ServiceResult saved = await _setup.SavePricesAsync(_org, [new CoursePriceInput(_acting.Id, CoursePricing.Monthly, 120)], []);

        Assert.Contains("This month's unpaid fees were brought up to date: 1 changed.", saved.Message);
        Assert.Equal(120m, Charges().Single().Amount);
        Assert.Equal(100m, Charges(payer).Single().Amount);
    }

    // ---- Payments ----

    [Fact]
    public async Task Payments_CanBePartial_NeverMoreThanOwed_AndTheRestCanBeWaived()
    {
        await _billing.EnsureChargesAsync(_org);
        Charge fee = Charges().Single();
        DateOnly today = new(2026, 9, 1);

        Assert.Equal("That's more than the €100.00 still owed for Acting, September 2026.",
            (await _billing.RecordPaymentAsync(_org, fee.Id, new PaymentInput(150, today, PaymentMethod.Cash, null), _admin.Id)).Error);
        Assert.Equal("The payment date can't be in the future.",
            (await _billing.RecordPaymentAsync(_org, fee.Id, new PaymentInput(10, today.AddDays(1), PaymentMethod.Cash, null), _admin.Id)).Error);

        ServiceResult part = await _billing.RecordPaymentAsync(_org, fee.Id, new PaymentInput(60, today, PaymentMethod.BankTransfer, "TR-1"), _admin.Id);
        Assert.Contains("€40.00 still to pay", part.Message);
        Assert.Contains(_db.Notifications.Where(n => n.UserId == _student.Id), n => n.Text.Contains("Payment of €60.00 received"));

        // After the due day, what's left is overdue; waiving it clears the books but keeps the line.
        SetSchoolTime(9, 7, 10);
        Books books = await _billing.GetBooksAsync(_org, BooksFilter.Overdue, null);
        Assert.Equal(40m, books.Overdue);
        Assert.Single(books.Charges);

        Assert.True((await _billing.WaiveChargeAsync(_org, fee.Id, "Moved away")).Succeeded);
        Charge waived = Charges().Single();
        Assert.Equal(0m, waived.Balance);
        Assert.Equal("Moved away", waived.CancelReason);
        Assert.Equal(0m, (await _billing.GetBooksAsync(_org, BooksFilter.Overdue, null)).Overdue);
    }

    [Fact]
    public async Task Reminders_GoOnce_AFewDaysBeforeTheDueDate()
    {
        Settings(billNoShows: false, dueDay: 10, reminderDays: 3);
        await _billing.EnsureChargesAsync(_org);
        Assert.Equal(0, await _billing.SendRemindersAsync());

        SetSchoolTime(9, 7, 9);
        Assert.Equal(1, await _billing.SendRemindersAsync());
        Assert.Equal(0, await _billing.SendRemindersAsync());
        Assert.Contains(_db.Notifications.Where(n => n.UserId == _student.Id),
            n => n.Text == "Atelier: €100.00 for Acting, September 2026 is due on Thursday 10 September.");
    }

    // ---- Checking in to per-class courses ----

    [Fact]
    public async Task CheckingIn_KeepsWithinThePlaces_AndSaysWhenToCancelBy()
    {
        Settings(billNoShows: true, cancelHours: 4);
        var others = Enumerable.Range(1, 3).Select(i => AddStudent($"drawer{i}")).ToList();
        others.ForEach(s => Enrol(s, _drawingA));

        ServiceResult booked = await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday);
        Assert.Equal("You're checked in to Life drawing on Wednesday 2 September at 19:00. If you can't come, cancel by Wed 2 Sept 15:00, or you'll be billed for it.",
            booked.Message);
        Assert.False((await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday)).Succeeded);
        await _bookings.BookAsync(others[0].Id, _drawingClass.Id, Wednesday);
        await _bookings.BookAsync(others[1].Id, _drawingClass.Id, Wednesday);

        Assert.Equal("Life drawing on Wednesday 2 September at 19:00 is full (3 places). Try another class.",
            (await _bookings.BookAsync(others[2].Id, _drawingClass.Id, Wednesday)).Error);
        Assert.False((await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday.AddDays(1))).Succeeded);
        Assert.False((await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday.AddDays(28))).Succeeded);

        // Four students in a group of three places: enrolment isn't limited by places in a per-class course.
        Assert.Equal(4, _db.Enrolments.Count(e => e.GroupId == _drawingA.Id));
        ClassDate shown = (await _bookings.GetUpcomingForStudentAsync(others[2].Id)).First();
        Assert.Equal((3, 0), (shown.Taken, shown.Left));
    }

    [Fact]
    public async Task PerClassCharges_CountWhatWasAttended_OrNotCancelledInTime_WhenNoShowsAreBilled()
    {
        Settings(billNoShows: true, cancelHours: 4);
        DateOnly[] wednesdays = [Wednesday, Wednesday.AddDays(7), Wednesday.AddDays(14), Wednesday.AddDays(21)];
        foreach (DateOnly date in wednesdays)
        {
            Assert.True((await _bookings.BookAsync(_student.Id, _drawingClass.Id, date)).Succeeded);
        }

        // Cancelled in time: free. Cancelled two hours before: billed. Didn't come: billed. Came: billed.
        int Booking(DateOnly date) => _db.Bookings.Single(b => b.Date == date).Id;
        Assert.Equal("Check-in to Life drawing on Wednesday 2 September at 19:00 cancelled.",
            (await _bookings.CancelAsync(_student.Id, Booking(wednesdays[0]))).Message);
        SetSchoolTime(9, 9, 17);
        Assert.Contains("so it will still be billed (€15.00)", (await _bookings.CancelAsync(_student.Id, Booking(wednesdays[1]))).Message);
        SetSchoolTime(9, 16, 21);
        Assert.True((await _bookings.SetNoShowAsync(_org, Booking(wednesdays[2]), true, teacherId: null)).Succeeded);

        // Nothing is billed until the month is over, but the student sees it coming.
        SetSchoolTime(9, 30, 22);
        await _billing.EnsureChargesAsync(_org);
        Assert.DoesNotContain(Charges(), c => c.Kind == ChargeKind.PerClass);
        Statement statement = (await _billing.GetStatementAsync(_org, _student.Id))!;
        Assert.Equal((3, 45m), (statement.SoFar.Single().Classes, statement.SoFar.Single().Amount));

        SetSchoolTime(10, 1, 9);
        await _billing.EnsureChargesAsync(_org);
        Charge classes = Charges().Single(c => c.Kind == ChargeKind.PerClass);
        Assert.Equal("Life drawing, September 2026: 3 classes × €15.00", classes.Description);
        Assert.Equal(45m, classes.Amount);
        Assert.Equal(new DateOnly(2026, 10, 5), classes.DueOn);

        // The billed month is closed: its check-ins can't be changed behind the charge.
        Assert.Contains("September 2026 is already billed", (await _bookings.SetNoShowAsync(_org, Booking(wednesdays[3]), true, null)).Error);
    }

    [Fact]
    public async Task SoFarThisMonth_CountsOnlyThisMonthsClassesUpToToday_AndNextMonthIsBilledInItsOwnMonth()
    {
        Settings(billNoShows: false);
        SetSchoolTime(9, 29, 10);
        DateOnly september = new(2026, 9, 30), october = new(2026, 10, 7);
        await _bookings.BookAsync(_student.Id, _drawingClass.Id, september);
        await _bookings.BookAsync(_student.Id, _drawingClass.Id, october);

        // The 30th hasn't come yet: nothing so far.
        Assert.Empty((await _billing.GetStatementAsync(_org, _student.Id))!.SoFar);
        SetSchoolTime(9, 30, 22);
        Assert.Equal(1, (await _billing.GetStatementAsync(_org, _student.Id))!.SoFar.Single().Classes);

        SetSchoolTime(10, 1, 9);
        await _billing.EnsureChargesAsync(_org);
        Assert.Equal("Life drawing, September 2026: 1 class × €15.00", Charges().Single(c => c.Kind == ChargeKind.PerClass).Description);
        SetSchoolTime(11, 1, 9);
        await _billing.EnsureChargesAsync(_org);
        Assert.Equal(["Life drawing, September 2026: 1 class × €15.00", "Life drawing, October 2026: 1 class × €15.00"],
            Charges().Where(c => c.Kind == ChargeKind.PerClass).Select(c => c.Description));
    }

    [Fact]
    public async Task CheckingInInsideTheCancellationWindow_SaysCancellingStillBills()
    {
        Settings(billNoShows: true, cancelHours: 4);
        SetSchoolTime(9, 2, 17);

        ServiceResult booked = await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday);

        Assert.Equal("You're checked in to Life drawing on Wednesday 2 September at 19:00. It's less than 4 hours before the class, " +
                     "so if you cancel now you'll still be billed (€15.00).", booked.Message);
        await _bookings.CancelAsync(_student.Id, _db.Bookings.Single().Id);
        Assert.True((await _bookings.GetUpcomingForStudentAsync(_student.Id)).First().CancelledLate);
    }

    [Fact]
    public async Task WithoutBillingNoShows_OnlyClassesAttendedAreCharged()
    {
        Settings(billNoShows: false);
        await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday);
        await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday.AddDays(7));
        SetSchoolTime(9, 2, 18);
        Assert.Equal("Check-in to Life drawing on Wednesday 2 September at 19:00 cancelled.",
            (await _bookings.CancelAsync(_student.Id, _db.Bookings.Single(b => b.Date == Wednesday).Id)).Message);

        int missed = _db.Bookings.Single(b => b.Date == Wednesday.AddDays(7)).Id;
        SetSchoolTime(9, 9, 18);
        Assert.Equal("The class hasn't started yet: mark who didn't come once it has.",
            (await _bookings.SetNoShowAsync(_org, missed, true, null)).Error);
        SetSchoolTime(9, 9, 22);
        Assert.True((await _bookings.SetNoShowAsync(_org, missed, true, null)).Succeeded);

        SetSchoolTime(10, 1, 9);
        await _billing.EnsureChargesAsync(_org);
        Assert.DoesNotContain(Charges(), c => c.Kind == ChargeKind.PerClass);
    }

    // ---- Classes that change after students checked in (found while building milestone 5) ----

    [Fact]
    public async Task TakingAClassOffTheTimetable_CancelsUpcomingCheckIns_AndStillBillsPastOnes()
    {
        Settings(billNoShows: false);
        await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday);
        await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday.AddDays(7));
        SetSchoolTime(9, 3, 10); // after the first class

        ServiceResult removed = await _schedule.DeleteAsync(_org, _drawingClass.Id);

        Assert.True(removed.Succeeded, removed.Error);
        Assert.Contains("1 upcoming check-in was cancelled", removed.Message);
        Booking past = _db.Bookings.AsNoTracking().Single();
        Assert.Equal((Wednesday, (int?)null), (past.Date, past.SessionId));
        Assert.Contains(_db.Notifications.Where(n => n.UserId == _student.Id), n =>
            n.Text == "Atelier: Your check-in to Life drawing on Wednesday 9 September at 19:00 was cancelled because the class was taken off " +
                      "the timetable. You won't be billed for it: check in to another class on your Schedule.");

        // The class that did happen is billed at the end of the month, though its weekly class is gone.
        SetSchoolTime(10, 1, 9);
        await _billing.EnsureChargesAsync(_org);
        Assert.Equal("Life drawing, September 2026: 1 class × €15.00", Charges().Single(c => c.Kind == ChargeKind.PerClass).Description);
    }

    [Fact]
    public async Task MovingAClass_CancelsItsUpcomingCheckIns_ButANewRoomAloneDoesnt()
    {
        await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday);
        int studio = _db.Classrooms.Single().Id;
        var otherRoom = new Classroom { OrganizationId = _org, Name = "Loft", Capacity = 10 };
        _db.Classrooms.Add(otherRoom);
        _db.SaveChanges();

        await _schedule.UpdateAsync(_org, _drawingClass.Id, new SessionInput(_drawingA.Id, otherRoom.Id, DayOfWeek.Wednesday, new TimeOnly(19, 0)));
        Assert.Single(_db.Bookings);

        ServiceResult moved = await _schedule.UpdateAsync(_org, _drawingClass.Id, new SessionInput(_drawingA.Id, studio, DayOfWeek.Thursday, new TimeOnly(19, 0)));

        Assert.Contains("1 upcoming check-in was cancelled", moved.Message);
        Assert.Empty(_db.Bookings);
    }

    [Fact]
    public async Task LeavingAGroup_CancelsTheStudentsUpcomingCheckIns()
    {
        await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday);
        int enrolment = _db.Enrolments.Single(e => e.StudentId == _student.Id && e.GroupId == _drawingA.Id).Id;

        await _enrolments.UnenrolAsync(_org, enrolment);

        Assert.Empty(_db.Bookings);
        Assert.Contains(_db.Notifications.Where(n => n.UserId == _student.Id), n => n.Text.Contains("because you're no longer enrolled in Life drawing"));
    }

    [Fact]
    public async Task ACourseWithCheckInsAndDiscounts_CanBeDeleted()
    {
        await _setup.GiveDiscountAsync(_org, _student.Id, new StudentDiscountInput(null, 10, "Early bird", _drawing.Id, null));
        await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday);

        ServiceResult deleted = await _courses.DeleteAsync(_org, _drawing.Id);

        Assert.True(deleted.Succeeded, deleted.Error);
        Assert.Empty(_db.Bookings);
        Assert.Empty(_db.StudentDiscounts);
    }

    [Fact]
    public async Task ATeacher_SeesOnlyTheirOwnClassesCheckIns()
    {
        ApplicationUser teacher = AddUser("teacher");
        _db.Memberships.Add(new Membership { UserId = teacher.Id, OrganizationId = _org, Role = MembershipRole.Teacher, Status = MembershipStatus.Active });
        _db.CourseGroups.Single(g => g.Id == _drawingA.Id).TeacherId = teacher.Id;
        _db.SaveChanges();
        await _bookings.BookAsync(_student.Id, _drawingClass.Id, Wednesday);

        ClassRoster roster = (await _bookings.GetRosterAsync(_org, _drawingClass.Id, Wednesday, teacher.Id))!;
        Assert.Equal(_student.Id, roster.Bookings.Single().StudentId);
        Assert.False(roster.Started);
        Assert.Null(await _bookings.GetRosterAsync(_org, _drawingClass.Id, Wednesday, _admin.Id));
        Assert.Contains(await _bookings.GetForTeacherAsync(teacher.Id), c => c.Date == Wednesday && c.Taken == 1);
    }

    [Fact]
    public async Task OneStudentsBooks_DontShowAnotherOrganisationsCharges()
    {
        await _billing.EnsureChargesAsync(_org);
        var other = new Organization
        {
            Name = "Elsewhere", City = "Patras", ContactEmail = "y@test.example", Status = OrganizationStatus.Active, InviteCode = "ELSE1", RequestedById = _admin.Id,
        };
        _db.Organizations.Add(other);
        _db.SaveChanges();

        Assert.Null(await _billing.GetStatementAsync(other.Id, _student.Id));
        Assert.Empty((await _billing.GetBooksAsync(other.Id, BooksFilter.All, null)).Charges);
        Assert.Equal(["Atelier"], (await _billing.GetMineAsync(_student.Id)).Select(s => s.Organization.Name));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
