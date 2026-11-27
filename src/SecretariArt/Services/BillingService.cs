using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

public interface IBillingService
{
    // Makes any charges that are due by now: this month's fees, and last month's classes. Safe to run any time.
    // Returns how many charges it made.
    Task<int> EnsureChargesAsync(int organizationId, string? studentId = null);
    Task<int> SendRemindersAsync();

    // Unpaid fees for this month follow today's prices and discounts. Returns how many changed.
    Task<RepriceResult> RepriceAsync(int organizationId, string? studentId);

    Task<Books> GetBooksAsync(int organizationId, BooksFilter filter, string? search);
    Task<Statement?> GetStatementAsync(int organizationId, string studentId);
    Task<List<Statement>> GetMineAsync(string studentId);

    Task<ServiceResult> RecordPaymentAsync(int organizationId, int chargeId, PaymentInput input, string recordedById);
    Task<ServiceResult> DeletePaymentAsync(int organizationId, int paymentId);
    Task<ServiceResult> AddChargeAsync(int organizationId, string studentId, ChargeInput input);
    Task<ServiceResult> UpdateChargeAsync(int organizationId, int chargeId, decimal amount, DateOnly dueOn);
    Task<ServiceResult> WaiveChargeAsync(int organizationId, int chargeId, string reason);
    Task<Charge?> FindChargeAsync(int organizationId, int chargeId);
}

public class BillingService(SecretariArtDbContext db, INotificationService notifications, TimeProvider clock) : IBillingService
{
    public async Task<int> EnsureChargesAsync(int organizationId, string? studentId = null)
    {
        DateOnly today = clock.SchoolToday();
        DateOnly month = today.FirstOfMonth();
        OrganizationRules? rules = await db.OrganizationRules.AsNoTracking().FirstOrDefaultAsync(r => r.OrganizationId == organizationId);

        var students = (await db.Memberships.AsNoTracking().Approved(MembershipRole.Student)
                .Where(m => m.OrganizationId == organizationId && (studentId == null || m.UserId == studentId))
                .Select(m => m.UserId).ToListAsync()).ToHashSet();
        var enrolments = (await EnrolmentsAsync(organizationId, studentId)).Where(e => students.Contains(e.StudentId)).ToList();
        var existingKeys = await db.Charges
            .Where(c => c.OrganizationId == organizationId && c.Key != null && (studentId == null || c.StudentId == studentId))
            .Select(c => c.Key!).ToListAsync();
        var run = new BillingRun(
            organizationId, await db.PaymentSettings.ForOrganizationAsync(organizationId), today, clock.GetUtcNow().UtcDateTime,
            await DiscountsAsync(organizationId, studentId), existingKeys.ToHashSet(),
            BundleCoverage(enrolments, await SettledCourseFeesAsync(organizationId, studentId, month), out _));

        // Monthly fees, for this month, if the term runs in it.
        var added = rules.TermRunsIn(month) ? MonthlyCharges(run, month, enrolments) : [];

        // Classes checked in to in months that are over. By the course on the check-in, so a class taken off the
        // timetable since is still billed.
        var perClassCourses = await db.Courses.PerClassAsync(organizationId);
        var bookings = (await db.Bookings
                .Where(b => b.OrganizationId == organizationId && b.ChargeId == null && b.Date < month &&
                            (studentId == null || b.StudentId == studentId))
                .ToListAsync())
            .Where(b => perClassCourses.ContainsKey(b.CourseId)).ToList();
        var perClass = PerClassCharges(run, bookings, perClassCourses);
        added.AddRange(perClass.Select(p => p.Charge));

        return await SaveNewChargesAsync(added, perClass);
    }

    // What every charge made in one billing run shares. Keys holds every charge key that exists, and grows as charges
    // are made, so nothing is billed twice.
    private sealed record BillingRun(
        int OrganizationId, PaymentSettings Settings, DateOnly Today, DateTime Now, List<StudentDiscount> Discounts,
        HashSet<string> Keys, Dictionary<(string StudentId, int CourseId), Bundle> Bundled)
    {
        public Charge NewCharge(string studentId, ChargeKind kind, string key, int? courseId, int? bundleId, DateOnly month,
            string description, int? classes, decimal baseAmount, DateOnly dueMonth) =>
            BillingService.NewCharge(OrganizationId, studentId, kind, key, courseId, bundleId, month, description, classes,
                baseAmount, Discounts, Due(dueMonth, Settings, Today), Now);
    }

    // This month's fees: a priced bundle's price for students who take all of it, each monthly course's price otherwise.
    private static List<Charge> MonthlyCharges(BillingRun run, DateOnly month, List<Enrolment> enrolments)
    {
        var charges = new List<Charge>();
        foreach (var group in run.Bundled.GroupBy(b => (b.Key.StudentId, BundleId: b.Value.Id)))
        {
            Bundle bundle = group.First().Value;
            string key = ChargeKeys.Bundle(bundle.Id, group.Key.StudentId, month);
            if (run.Keys.Add(key))
            {
                charges.Add(run.NewCharge(group.Key.StudentId, ChargeKind.Bundle, key, null, bundle.Id, month,
                    $"{bundle.Name} (bundle), {DisplayNames.MonthName(month)}", null, bundle.MonthlyPrice!.Value, month));
            }
        }

        var monthly = enrolments.Where(e => !run.Bundled.ContainsKey((e.StudentId, e.CourseId)) &&
                                            e.Course!.Pricing == CoursePricing.Monthly && e.Course.Price > 0);
        foreach (Enrolment enrolment in monthly)
        {
            string key = ChargeKeys.Monthly(enrolment.CourseId, enrolment.StudentId, month);
            if (run.Keys.Add(key))
            {
                charges.Add(run.NewCharge(enrolment.StudentId, ChargeKind.Monthly, key, enrolment.CourseId, null, month,
                    $"{enrolment.Course!.Name}, {DisplayNames.MonthName(month)}", null, enrolment.Course.Price, month));
            }
        }
        return charges;
    }

    // One charge per student, course and month for the billable check-ins (due the next month), with the check-ins it
    // covers. A course covered by a priced bundle isn't charged per class.
    private static List<(Charge Charge, List<Booking> Bookings)> PerClassCharges(
        BillingRun run, List<Booking> bookings, Dictionary<int, Course> courses)
    {
        var charges = new List<(Charge, List<Booking>)>();
        foreach (var group in bookings.GroupBy(b => (b.StudentId, b.CourseId, Month: b.Date.FirstOfMonth())))
        {
            var (student, courseId, billedMonth) = group.Key;
            Course course = courses[courseId];
            int billable = group.Count(b => BookingService.Billable(b, run.Settings));
            string key = ChargeKeys.PerClass(course.Id, student, billedMonth);
            if (billable == 0 || course.Price <= 0 || run.Bundled.ContainsKey((student, course.Id)) || !run.Keys.Add(key))
            {
                continue;
            }
            string description = $"{course.Name}, {DisplayNames.MonthName(billedMonth)}: " +
                                 $"{DisplayNames.Plural(billable, "class", "classes")} × {DisplayNames.Money(course.Price)}";
            charges.Add((run.NewCharge(student, ChargeKind.PerClass, key, course.Id, null, billedMonth, description,
                billable, course.Price * billable, billedMonth.AddMonths(1)), group.ToList()));
        }
        return charges;
    }

    private async Task<int> SaveNewChargesAsync(List<Charge> added, List<(Charge Charge, List<Booking> Bookings)> perClass)
    {
        if (added.Count == 0)
        {
            return 0;
        }
        db.Charges.AddRange(added);
        try
        {
            await db.SaveChangesAsync();
            // The month's check-ins now belong to its charge: they can't be changed behind it.
            foreach (var (charge, these) in perClass)
            {
                these.ForEach(b => b.ChargeId = charge.Id);
            }
            await db.SaveChangesAsync();
            return added.Count;
        }
        catch (DbUpdateException)
        {
            // Another request (or the hourly run) made the same charges a moment ago: theirs stand.
            db.ChangeTracker.Clear();
            return 0;
        }
    }

    public async Task<int> SendRemindersAsync()
    {
        DateOnly today = clock.SchoolToday();
        var settings = await db.PaymentSettings.AsNoTracking().ToDictionaryAsync(s => s.OrganizationId);
        var candidates = await db.Charges.Include(c => c.Payments).Include(c => c.Organization)
            .Where(c => c.ReminderSentAt == null && c.CancelledAt == null && c.DueOn >= today && c.Organization!.Status == OrganizationStatus.Active)
            .ToListAsync();

        int sent = 0;
        foreach (Charge charge in candidates)
        {
            int days = settings.TryGetValue(charge.OrganizationId, out PaymentSettings? s) ? s.ReminderDays : PaymentDefaults.ReminderDays;
            if (days == 0 || charge.Balance == 0 || charge.DueOn > today.AddDays(days))
            {
                continue;
            }
            notifications.Add([charge.StudentId], charge.OrganizationId,
                $"{charge.Organization!.Name}: {DisplayNames.Money(charge.Balance)} for {charge.Description} is due {(charge.DueOn == today ? "today" : $"on {DisplayNames.LongDate(charge.DueOn)}")}.",
                "/payments");
            charge.ReminderSentAt = clock.GetUtcNow().UtcDateTime;
            sent++;
        }
        await db.SaveChangesAsync();
        return sent;
    }

    public async Task<RepriceResult> RepriceAsync(int organizationId, string? studentId)
    {
        DateOnly month = clock.SchoolToday().FirstOfMonth();
        var charges = await db.Charges.Include(c => c.Payments)
            .Where(c => c.OrganizationId == organizationId && c.Month == month && c.CancelledAt == null &&
                        (c.Kind == ChargeKind.Monthly || c.Kind == ChargeKind.Bundle) && !c.Payments.Any() &&
                        (studentId == null || c.StudentId == studentId))
            .ToListAsync();

        var courses = await db.Courses.AsNoTracking().Where(c => c.OrganizationId == organizationId).ToDictionaryAsync(c => c.Id);
        var bundled = BundleCoverage(await EnrolmentsAsync(organizationId, studentId), await SettledCourseFeesAsync(organizationId, studentId, month), out var waiting);
        var discounts = await DiscountsAsync(organizationId, studentId);

        int updated = 0, removed = 0;
        foreach (Charge charge in charges)
        {
            decimal price = CurrentPrice(charge, bundled, courses);
            if (price <= 0)
            {
                db.Charges.Remove(charge);
                removed++;
                continue;
            }

            var (discount, note) = ApplyDiscounts(price, Applicable(discounts, charge.StudentId, charge.CourseId, charge.BundleId));
            if (charge.BaseAmount != price || charge.DiscountAmount != discount)
            {
                charge.BaseAmount = price;
                charge.DiscountAmount = discount;
                charge.DiscountNote = note;
                charge.Amount = price - discount;
                updated++;
            }
        }
        await db.SaveChangesAsync();

        // Then make what's due now in their place (a bundle fee for the course fees just removed, or the other way round).
        int added = await EnsureChargesAsync(organizationId, studentId);
        var waitingNames = await db.Users.Where(u => waiting.Contains(u.Id)).OrderBy(u => u.FirstName).Select(u => u.FirstName + " " + u.LastName).ToListAsync();
        return new RepriceResult(updated, removed, added, waitingNames);
    }

    // What a monthly fee should be now, or 0 if it shouldn't exist any more. A bundle fee while the student takes the
    // whole priced bundle; a course fee while the course is priced monthly and not covered by such a bundle (a bundle
    // price set later replaces the course fees already made, rather than adding to them).
    private static decimal CurrentPrice(
        Charge charge, Dictionary<(string StudentId, int CourseId), Bundle> bundled, Dictionary<int, Course> courses)
    {
        if (charge.Kind == ChargeKind.Bundle)
        {
            Bundle? bundle = bundled.FirstOrDefault(b => b.Key.StudentId == charge.StudentId && b.Value.Id == charge.BundleId).Value;
            return bundle?.MonthlyPrice ?? 0;
        }

        int courseId = charge.CourseId!.Value;
        bool coveredByBundle = bundled.ContainsKey((charge.StudentId, courseId));
        return !coveredByBundle && courses.GetValueOrDefault(courseId) is { Pricing: CoursePricing.Monthly } course ? course.Price : 0;
    }

    public async Task<Books> GetBooksAsync(int organizationId, BooksFilter filter, string? search)
    {
        await EnsureChargesAsync(organizationId);
        DateOnly today = clock.SchoolToday();
        DateOnly month = today.FirstOfMonth();

        // All in memory: SQLite (the tests) can't sum or compare decimals, and one organisation's books are small.
        var charges = await db.Charges.AsNoTracking().Include(c => c.Payments).Include(c => c.Student)
            .Where(c => c.OrganizationId == organizationId).ToListAsync();
        var students = await db.Memberships.AsNoTracking().Include(m => m.User)
            .Approved(MembershipRole.Student).Where(m => m.OrganizationId == organizationId)
            .Select(m => m.User!).ToListAsync();
        var discounted = (await db.StudentDiscounts.Where(d => d.OrganizationId == organizationId).Select(d => d.StudentId).ToListAsync()).ToHashSet();

        var matching = charges.Where(c => filter switch
            {
                BooksFilter.Open => c.Balance > 0,
                BooksFilter.Overdue => c.IsOverdue(today),
                BooksFilter.Paid => c.IsPaid || c.CancelledAt != null,
                _ => true,
            })
            .Where(c => string.IsNullOrWhiteSpace(search) || TextMatch.Contains(c.Student!.FullName, search) || TextMatch.Contains(c.Description, search))
            .ToList();
        // Open and overdue lists: overdue first, then the soonest due. Paid and all: the latest first.
        bool latestFirst = filter is BooksFilter.Paid or BooksFilter.All;
        matching = matching
            .OrderBy(c => latestFirst || c.IsOverdue(today) ? 0 : 1)
            .ThenBy(c => latestFirst ? -c.DueOn.DayNumber : c.DueOn.DayNumber)
            .ThenBy(c => c.Student!.LastName).ThenBy(c => c.Student!.FirstName)
            .ToList();

        var people = SummariseStudents(students.Concat(charges.Select(c => c.Student!)).DistinctBy(s => s.Id)
            .Where(s => string.IsNullOrWhiteSpace(search) || TextMatch.Contains(s.FullName, search)), charges, discounted, today);

        return new Books(matching, people,
            charges.Where(c => c.IsOverdue(today)).Sum(c => c.Balance),
            charges.Where(c => c.Balance > 0 && !c.IsOverdue(today) && c.DueOn <= today.AddDays(PaymentDefaults.DueSoonDays)).Sum(c => c.Balance),
            charges.SelectMany(c => c.Payments).Where(p => p.PaidOn >= month).Sum(p => p.Amount),
            today, charges.Count(c => c.IsOverdue(today)));
    }

    // Each student's totals for the books, the most overdue first.
    private static List<BooksStudent> SummariseStudents(
        IEnumerable<ApplicationUser> students, List<Charge> charges, HashSet<string> discounted, DateOnly today)
    {
        var byStudent = charges.ToLookup(c => c.StudentId);
        return students
            .Select(s =>
            {
                var theirs = byStudent[s.Id].ToList();
                return new BooksStudent(s,
                    Owed: theirs.Sum(c => c.Balance),
                    Overdue: theirs.Where(c => c.IsOverdue(today)).Sum(c => c.Balance),
                    NextDue: theirs.Where(c => c.Balance > 0).Select(c => (DateOnly?)c.DueOn).Min(),
                    HasDiscount: discounted.Contains(s.Id));
            })
            .OrderByDescending(s => s.Overdue).ThenByDescending(s => s.Owed)
            .ThenBy(s => s.Student.LastName).ThenBy(s => s.Student.FirstName)
            .ToList();
    }

    public async Task<Statement?> GetStatementAsync(int organizationId, string studentId)
    {
        ApplicationUser? student = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == studentId);
        bool known = await db.Memberships.HasApprovedAsync(studentId, organizationId, MembershipRole.Student)
                     || await db.Charges.AnyAsync(c => c.StudentId == studentId && c.OrganizationId == organizationId);
        if (student == null || !known)
        {
            return null;
        }

        await EnsureChargesAsync(organizationId, studentId);
        return await StatementAsync(organizationId, student);
    }

    public async Task<List<Statement>> GetMineAsync(string studentId)
    {
        ApplicationUser student = await db.Users.AsNoTracking().FirstAsync(u => u.Id == studentId);
        var organizationIds = await db.Memberships.Live()
            .Where(m => m.UserId == studentId && m.Role == MembershipRole.Student)
            .Select(m => m.OrganizationId)
            .Union(db.Charges.Where(c => c.StudentId == studentId).Select(c => c.OrganizationId))
            .ToListAsync();

        var result = new List<Statement>();
        foreach (int organizationId in organizationIds)
        {
            await EnsureChargesAsync(organizationId, studentId);
            result.Add(await StatementAsync(organizationId, student));
        }
        return result.OrderByDescending(s => s.Overdue).ThenBy(s => s.Organization.Name).ToList();
    }

    public async Task<ServiceResult> RecordPaymentAsync(int organizationId, int chargeId, PaymentInput input, string recordedById)
    {
        Charge? charge = await db.Charges.Include(c => c.Payments).Include(c => c.Student)
            .FirstOrDefaultAsync(c => c.Id == chargeId && c.OrganizationId == organizationId);
        if (charge == null)
        {
            return ChargeNotFound;
        }
        if (charge.CancelledAt != null)
        {
            return ServiceResult.Fail("That charge was waived, so there's nothing to pay.");
        }
        if (input.Amount <= 0)
        {
            return ServiceResult.Fail("Enter the amount received.");
        }
        if (charge.Balance == 0)
        {
            return ServiceResult.Fail($"{charge.Description} is already paid in full.");
        }
        if (input.Amount > charge.Balance)
        {
            return ServiceResult.Fail($"That's more than the {DisplayNames.Money(charge.Balance)} still owed for {charge.Description}.");
        }
        if (input.PaidOn > clock.SchoolToday())
        {
            return ServiceResult.Fail("The payment date can't be in the future.");
        }
        if (input.Method == PaymentMethod.Online)
        {
            return ServiceResult.Fail("Choose how it was paid: cash, card or bank transfer.");
        }

        charge.Payments.Add(new Payment
        {
            OrganizationId = organizationId, Amount = Math.Round(input.Amount, 2), PaidOn = input.PaidOn, Method = input.Method,
            Reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim(),
            RecordedById = recordedById, RecordedAt = clock.GetUtcNow().UtcDateTime,
        });
        bool settled = charge.Balance == 0;
        notifications.Add([charge.StudentId], organizationId,
            $"{await OrganizationNameAsync(organizationId)}: Payment of {DisplayNames.Money(input.Amount)} received for {charge.Description}." +
            (settled ? " It's paid in full." : $" {DisplayNames.Money(charge.Balance)} still to pay."), "/payments");
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{DisplayNames.Money(input.Amount)} from {charge.Student!.FullName} recorded for {charge.Description}." +
            (settled ? " Paid in full." : $" {DisplayNames.Money(charge.Balance)} still to pay."));
    }

    public async Task<ServiceResult> DeletePaymentAsync(int organizationId, int paymentId)
    {
        Payment? payment = await db.Payments.Include(p => p.Charge)
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.OrganizationId == organizationId);
        if (payment == null)
        {
            return ServiceResult.Fail("That payment was already removed.");
        }

        db.Payments.Remove(payment);
        // The student was told when it was recorded, so they're told it's gone (QA, P14).
        notifications.Add([payment.Charge!.StudentId], organizationId,
            $"{await OrganizationNameAsync(organizationId)}: The payment of {DisplayNames.Money(payment.Amount)} on {DisplayNames.ShortDate(payment.PaidOn)} " +
            $"for {payment.Charge.Description} was removed from your account, so it's owed again. Ask the office if you think that's a mistake.", "/payments");
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"Payment of {DisplayNames.Money(payment.Amount)} for {payment.Charge!.Description} removed: it's owed again.");
    }

    public async Task<ServiceResult> AddChargeAsync(int organizationId, string studentId, ChargeInput input)
    {
        ApplicationUser? student = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == studentId);
        if (student == null || !await db.Memberships.HasApprovedAsync(studentId, organizationId, MembershipRole.Student))
        {
            return ServiceResult.Fail("That person isn't an approved student of this organisation.");
        }
        if (input.Amount is <= 0 or > PaymentDefaults.MaxPrice)
        {
            return ServiceResult.Fail($"Enter an amount between €0.01 and {DisplayNames.Money(PaymentDefaults.MaxPrice)}.");
        }

        var charge = new Charge
        {
            OrganizationId = organizationId, StudentId = studentId, Kind = ChargeKind.Other, Description = input.Description.Trim(),
            BaseAmount = Math.Round(input.Amount, 2), Amount = Math.Round(input.Amount, 2), DueOn = input.DueOn,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Charges.Add(charge);
        notifications.Add([studentId], organizationId,
            $"{await OrganizationNameAsync(organizationId)}: New charge: {charge.Description}, {DisplayNames.Money(charge.Amount)}, due on {DisplayNames.LongDate(charge.DueOn)}.", "/payments");
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{charge.Description} ({DisplayNames.Money(charge.Amount)}) added to {student.FullName}'s account.", charge.Id);
    }

    public async Task<ServiceResult> UpdateChargeAsync(int organizationId, int chargeId, decimal amount, DateOnly dueOn)
    {
        Charge? charge = await db.Charges.Include(c => c.Payments)
            .FirstOrDefaultAsync(c => c.Id == chargeId && c.OrganizationId == organizationId);
        if (charge == null)
        {
            return ChargeNotFound;
        }
        if (charge.CancelledAt != null)
        {
            return ServiceResult.Fail("That charge was waived; it can't be changed.");
        }
        if (amount < charge.Paid || amount <= 0 || amount > PaymentDefaults.MaxPrice)
        {
            return ServiceResult.Fail(charge.Paid > 0
                ? $"The amount can't be less than the {DisplayNames.Money(charge.Paid)} already paid."
                : "Enter an amount above €0. To drop the charge, waive it.");
        }

        bool dueChanged = charge.DueOn != dueOn;
        charge.Amount = Math.Round(amount, 2);
        charge.DueOn = dueOn;
        if (dueChanged)
        {
            // A new due date earns a new reminder.
            charge.ReminderSentAt = null;
        }
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{charge.Description} saved: {DisplayNames.Money(charge.Amount)}, due on {DisplayNames.LongDate(charge.DueOn)}.");
    }

    public async Task<ServiceResult> WaiveChargeAsync(int organizationId, int chargeId, string reason)
    {
        Charge? charge = await db.Charges.Include(c => c.Payments)
            .FirstOrDefaultAsync(c => c.Id == chargeId && c.OrganizationId == organizationId);
        if (charge == null)
        {
            return ChargeNotFound;
        }
        if (charge.CancelledAt != null)
        {
            return ServiceResult.Fail("That charge was already waived.");
        }
        if (charge.Balance == 0)
        {
            return ServiceResult.Fail("That charge is paid in full: there's nothing left to waive.");
        }

        decimal waived = charge.Balance;
        charge.CancelledAt = clock.GetUtcNow().UtcDateTime;
        charge.CancelReason = reason.Trim();
        notifications.Add([charge.StudentId], organizationId,
            $"{await OrganizationNameAsync(organizationId)}: {DisplayNames.Money(waived)} for {charge.Description} was waived: you don't need to pay it.", "/payments");
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{DisplayNames.Money(waived)} for {charge.Description} waived.");
    }

    public Task<Charge?> FindChargeAsync(int organizationId, int chargeId) =>
        db.Charges.AsNoTracking().Include(c => c.Payments).Include(c => c.Student)
            .FirstOrDefaultAsync(c => c.Id == chargeId && c.OrganizationId == organizationId);

    // ---- Working out amounts ----

    // Each discount takes its percentage off what's left after the ones before it, e.g. 25% then 10% is 32.5% off.
    public static (decimal Discount, string? Note) ApplyDiscounts(decimal baseAmount, IEnumerable<StudentDiscount> discounts)
    {
        decimal amount = baseAmount;
        var notes = new List<string>();
        foreach (StudentDiscount discount in discounts.OrderBy(d => d.Id))
        {
            decimal cut = Math.Round(amount * discount.EffectivePercent / 100, 2, MidpointRounding.AwayFromZero);
            amount -= cut;
            notes.Add($"{discount.Label} {discount.EffectivePercent:0.##}%");
        }
        return (baseAmount - amount, notes.Count == 0 ? null : string.Join(", ", notes));
    }

    // Discounts on everything apply to every fee; one on a course or bundle only to that course's or bundle's.
    public static IEnumerable<StudentDiscount> Applicable(IEnumerable<StudentDiscount> discounts, string studentId, int? courseId, int? bundleId) =>
        discounts.Where(d => d.StudentId == studentId &&
                             ((d.CourseId == null && d.BundleId == null) || (d.CourseId != null && d.CourseId == courseId) ||
                              (d.BundleId != null && d.BundleId == bundleId)));

    private static Charge NewCharge(
        int organizationId, string studentId, ChargeKind kind, string key, int? courseId, int? bundleId, DateOnly month,
        string description, int? classes, decimal baseAmount, IEnumerable<StudentDiscount> discounts, DateOnly dueOn, DateTime now)
    {
        var (discount, note) = ApplyDiscounts(baseAmount, Applicable(discounts, studentId, courseId, bundleId));
        return new Charge
        {
            OrganizationId = organizationId, StudentId = studentId, Kind = kind, Key = key, CourseId = courseId, BundleId = bundleId,
            Month = month, Description = description, Classes = classes, BaseAmount = baseAmount, DiscountAmount = discount,
            DiscountNote = note, Amount = baseAmount - discount, DueOn = dueOn, CreatedAt = now,
        };
    }

    // Due on the organisation's day of the given month; if that's passed already, a week from today.
    private static DateOnly Due(DateOnly month, PaymentSettings settings, DateOnly today)
    {
        DateOnly due = month.Day(settings.DueDay);
        return due >= today ? due : today.AddDays(PaymentDefaults.LateChargeDays);
    }

    // The courses each student takes through a priced bundle, with that bundle. The bundle price applies only to a
    // student who takes every course of the bundle through it; someone taking part of it pays those courses' own
    // prices (QA, P2). A student who already paid (or was let off) a course fee of the bundle this month keeps this
    // month's course fees, and the bundle price starts next month: never both for the same course (QA, P11).
    private static Dictionary<(string StudentId, int CourseId), Bundle> BundleCoverage(
        IEnumerable<Enrolment> enrolments, IReadOnlySet<(string StudentId, int CourseId)> settled, out HashSet<string> waiting)
    {
        var result = new Dictionary<(string StudentId, int CourseId), Bundle>();
        waiting = [];
        foreach (var group in enrolments.Where(e => e.Bundle?.MonthlyPrice > 0).GroupBy(e => (e.StudentId, e.BundleId)))
        {
            Bundle bundle = group.First().Bundle!;
            var taken = group.Select(e => e.CourseId).ToHashSet();
            if (bundle.Courses.Count == 0 || !bundle.Courses.All(l => taken.Contains(l.CourseId)))
            {
                continue;
            }
            if (group.Any(e => settled.Contains((e.StudentId, e.CourseId))))
            {
                waiting.Add(group.Key.StudentId);
                continue;
            }
            foreach (Enrolment enrolment in group)
            {
                result[(enrolment.StudentId, enrolment.CourseId)] = bundle;
            }
        }
        return result;
    }

    // This month's course fees that are paid, part paid or waived: they stay as they are.
    private async Task<HashSet<(string StudentId, int CourseId)>> SettledCourseFeesAsync(int organizationId, string? studentId, DateOnly month) =>
        (await db.Charges.Where(c => c.OrganizationId == organizationId && c.Month == month && c.Kind == ChargeKind.Monthly &&
                                     (c.CancelledAt != null || c.Payments.Any()) && (studentId == null || c.StudentId == studentId))
            .Select(c => new { c.StudentId, CourseId = c.CourseId!.Value }).ToListAsync())
        .Select(c => (c.StudentId, c.CourseId)).ToHashSet();

    private Task<List<Enrolment>> EnrolmentsAsync(int organizationId, string? studentId) =>
        db.Enrolments.AsNoTracking().Include(e => e.Course).Include(e => e.Group).Include(e => e.Bundle).ThenInclude(b => b!.Courses)
            .Where(e => e.OrganizationId == organizationId && (studentId == null || e.StudentId == studentId))
            .ToListAsync();

    private async Task<string> OrganizationNameAsync(int organizationId) =>
        await db.Organizations.Where(o => o.Id == organizationId).Select(o => o.Name).FirstAsync();

    private async Task<Statement> StatementAsync(int organizationId, ApplicationUser student)
    {
        DateOnly today = clock.SchoolToday();
        DateOnly month = today.FirstOfMonth();
        PaymentSettings settings = await db.PaymentSettings.ForOrganizationAsync(organizationId);
        Organization organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == organizationId);
        var charges = await db.Charges.AsNoTracking().Include(c => c.Payments).ThenInclude(p => p.RecordedBy)
            .Where(c => c.OrganizationId == organizationId && c.StudentId == student.Id).ToListAsync();
        var enrolments = await EnrolmentsAsync(organizationId, student.Id);
        var discounts = await db.StudentDiscounts.AsNoTracking().Include(d => d.Discount).Include(d => d.Course).Include(d => d.Bundle)
            .Where(d => d.OrganizationId == organizationId && d.StudentId == student.Id).OrderBy(d => d.Id).ToListAsync();

        var bundled = BundleCoverage(enrolments, await SettledCourseFeesAsync(organizationId, student.Id, month), out _)
            .ToDictionary(b => b.Key.CourseId, b => b.Value);
        var soFar = await ClassesSoFarAsync(organizationId, student.Id, settings, discounts, bundled, month, today);

        var ordered = charges
            .OrderBy(c => c.Balance > 0 ? 0 : 1)
            .ThenBy(c => c.Balance > 0 ? c.DueOn.DayNumber : -c.DueOn.DayNumber)
            .ThenBy(c => c.Description).ToList();
        return new Statement(organization, student, ordered, discounts, enrolments.OrderBy(e => e.Course!.Name).ToList(), bundled, soFar, today);
    }

    // This month's check-ins up to today, priced as they will be billed next month, per course. Courses covered by a
    // priced bundle are paid through the bundle.
    private async Task<List<ClassesSoFar>> ClassesSoFarAsync(int organizationId, string studentId, PaymentSettings settings,
        List<StudentDiscount> discounts, Dictionary<int, Bundle> bundled, DateOnly month, DateOnly today)
    {
        var perClassCourses = await db.Courses.PerClassAsync(organizationId);
        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => b.OrganizationId == organizationId && b.StudentId == studentId && b.Date >= month && b.Date <= today &&
                        b.ChargeId == null)
            .ToListAsync();
        return bookings
            .Where(b => perClassCourses.ContainsKey(b.CourseId) && BookingService.Billable(b, settings) && !bundled.ContainsKey(b.CourseId))
            .GroupBy(b => b.CourseId)
            .Select(g =>
            {
                Course course = perClassCourses[g.Key];
                decimal amount = course.Price * g.Count();
                var (discount, _) = ApplyDiscounts(amount, Applicable(discounts, studentId, course.Id, null));
                return new ClassesSoFar(course, g.Count(), amount - discount);
            })
            .Where(c => c.Amount > 0)
            .OrderBy(c => c.Course.Name).ToList();
    }

    private Task<List<StudentDiscount>> DiscountsAsync(int organizationId, string? studentId) =>
        db.StudentDiscounts.AsNoTracking().Include(d => d.Discount)
            .Where(d => d.OrganizationId == organizationId && (studentId == null || d.StudentId == studentId)).ToListAsync();

    private static readonly ServiceResult ChargeNotFound = ServiceResult.Fail("That charge doesn't exist any more.");
}
