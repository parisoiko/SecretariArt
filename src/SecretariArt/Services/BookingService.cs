using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

// One dated class of a per-class course: "Life drawing, group A, Thursday 27 November 18:00", with its places.
public record ClassDate(
    ClassSession Session, Organization Organization, DateOnly Date, int Places, int Taken, Booking? Mine, bool CancelledLate = false,
    decimal? YourPrice = null)
{
    public DateTime Starts => Date.ToDateTime(Session.Starts);
    public int Left => Math.Max(0, Places - Taken);

    // What one class costs this student, with their discounts (QA, P15); the course price when there's no student.
    public decimal Price => YourPrice ?? Session.Group!.Course!.Price;
}

// The people checked in to one dated class, for its teacher and the admins.
public record ClassRoster(ClassDate Class, IReadOnlyList<Booking> Bookings, PaymentSettings Settings, bool Started);

public interface IBookingService
{
    Task<List<ClassDate>> GetUpcomingForStudentAsync(string studentId);
    Task<ServiceResult> BookAsync(string studentId, int sessionId, DateOnly date);
    Task<ServiceResult> CancelAsync(string studentId, int bookingId);

    // Per-class classes from a week ago to two weeks ahead: a teacher's own, or all of an organisation's.
    Task<List<ClassDate>> GetForTeacherAsync(string teacherId);
    Task<List<ClassDate>> GetForOrganizationAsync(int organizationId);

    // teacherId: only if that person teaches the class; null for an admin of the organisation.
    Task<ClassRoster?> GetRosterAsync(int organizationId, int sessionId, DateOnly date, string? teacherId);
    Task<ServiceResult> SetNoShowAsync(int organizationId, int bookingId, bool noShow, string? teacherId);

    // Each organisation's cancellation rule, to say "cancel by 14:00" next to each check-in.
    Task<IReadOnlyDictionary<int, PaymentSettings>> GetSettingsAsync(IEnumerable<int> organizationIds);
}

public class BookingService(SecretariArtDbContext db, TimeProvider clock) : IBookingService
{
    private const int DaysBack = 7;
    private const int DaysAhead = 14;

    // Whether a check-in is charged: kept and attended; or, if the organisation bills no-shows, kept but not attended,
    // or cancelled too late.
    public static bool Billable(Booking booking, PaymentSettings settings) =>
        booking.CancelledAt == null ? !booking.NoShow || settings.BillNoShows : booking.CancelledLate && settings.BillNoShows;

    public async Task<List<ClassDate>> GetUpcomingForStudentAsync(string studentId)
    {
        DateTime now = clock.SchoolNow();
        DateOnly today = DateOnly.FromDateTime(now);
        var sessions = await PerClassSessions()
            .Where(s => s.Group!.Enrolments.Any(e => e.StudentId == studentId) &&
                        db.Memberships.Any(m => m.UserId == studentId && m.OrganizationId == s.OrganizationId &&
                                                m.Role == MembershipRole.Student && m.Status == MembershipStatus.Active))
            .ToListAsync();
        var classes = await DatesAsync(sessions, today, today.AddDays(PaymentDefaults.BookingDaysAhead - 1), studentId);
        var discounts = await DiscountsAsync(studentId);
        return classes.Where(c => c.Starts > now)
            .Select(c => c with { YourPrice = YourPrice(c.Session.Group!.Course!, studentId, discounts) }).ToList();
    }

    public async Task<ServiceResult> BookAsync(string studentId, int sessionId, DateOnly date)
    {
        ClassSession? session = await PerClassSessions()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.Group!.Enrolments.Any(e => e.StudentId == studentId));
        if (session == null || !await IsActiveStudentAsync(studentId, session.OrganizationId))
        {
            return ServiceResult.Fail("That class isn't one you can check in to. You can check in to the classes of per-class courses you're enrolled in.");
        }

        DateTime now = clock.SchoolNow();
        DateOnly today = DateOnly.FromDateTime(now);
        string name = $"{session.Group!.Course!.Name} on {DisplayNames.LongDate(date)} at {session.Starts:HH:mm}";
        if (date.DayOfWeek != session.Day || date < today || date > today.AddDays(PaymentDefaults.BookingDaysAhead - 1))
        {
            return ServiceResult.Fail($"You can check in to classes in the next {PaymentDefaults.BookingDaysAhead / 7} weeks: choose one from the list.");
        }
        if (date.ToDateTime(session.Starts) <= now)
        {
            return ServiceResult.Fail($"{name} has already started, so check-in is closed.");
        }
        OrganizationRules? rules = await db.OrganizationRules.AsNoTracking().FirstOrDefaultAsync(r => r.OrganizationId == session.OrganizationId);
        if (!InTerm(rules, date))
        {
            return ServiceResult.Fail($"{DisplayNames.LongDate(date)} is outside the term, so there's no class that day.");
        }

        Booking? booking = await db.Bookings.FirstOrDefaultAsync(b => b.SessionId == sessionId && b.Date == date && b.StudentId == studentId);
        if (booking is { CancelledAt: null })
        {
            return ServiceResult.Fail($"You're already checked in to {name}.");
        }
        int taken = await db.Bookings.CountAsync(b => b.SessionId == sessionId && b.Date == date && b.CancelledAt == null);
        if (taken >= Places(session))
        {
            return ServiceResult.Fail($"{name} is full ({Places(session)} places). Try another class.");
        }

        if (booking == null)
        {
            db.Bookings.Add(new Booking
            {
                OrganizationId = session.OrganizationId, SessionId = sessionId, Date = date, StudentId = studentId,
                BookedAt = clock.GetUtcNow().UtcDateTime,
            });
        }
        else
        {
            // Checking in again after cancelling: the same check-in comes back, no longer cancelled.
            booking.CancelledAt = null;
            booking.CancelledLate = false;
            booking.BookedAt = clock.GetUtcNow().UtcDateTime;
        }
        await db.SaveChangesAsync();

        PaymentSettings settings = await SettingsAsync(session.OrganizationId);
        DateTime deadline = date.ToDateTime(session.Starts).AddHours(-settings.CancelHours);
        // Inside the cancellation window already: say so, rather than give a deadline that has passed (QA, P10).
        if (settings.BillNoShows && now > deadline)
        {
            return ServiceResult.Ok($"You're checked in to {name}. It's less than {DisplayNames.Plural(settings.CancelHours, "hour")} before the class, so if you cancel now you'll still be billed ({DisplayNames.Money(YourPrice(session.Group.Course, studentId, await DiscountsAsync(studentId)))}).");
        }
        return ServiceResult.Ok(settings.BillNoShows
            ? $"You're checked in to {name}. If you can't come, cancel by {DisplayNames.ShortDate(DateOnly.FromDateTime(deadline))} {deadline:HH:mm}, or you'll be billed for it."
            : $"You're checked in to {name}. If you can't come, please cancel, so someone else can have your place.");
    }

    public async Task<ServiceResult> CancelAsync(string studentId, int bookingId)
    {
        Booking? booking = await db.Bookings.Include(b => b.Session).ThenInclude(s => s!.Group).ThenInclude(g => g!.Course)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.StudentId == studentId && b.CancelledAt == null);
        if (booking == null)
        {
            return ServiceResult.Fail("That check-in doesn't exist or was already cancelled.");
        }

        ClassSession session = booking.Session!;
        DateTime now = clock.SchoolNow();
        DateTime starts = booking.Date.ToDateTime(session.Starts);
        string name = $"{session.Group!.Course!.Name} on {DisplayNames.LongDate(booking.Date)} at {session.Starts:HH:mm}";
        if (starts <= now)
        {
            return ServiceResult.Fail($"{name} has already started, so the check-in can't be cancelled. If you couldn't come, tell the office.");
        }

        PaymentSettings settings = await SettingsAsync(booking.OrganizationId);
        booking.CancelledAt = clock.GetUtcNow().UtcDateTime;
        booking.CancelledLate = now > starts.AddHours(-settings.CancelHours);
        await db.SaveChangesAsync();

        return ServiceResult.Ok(booking.CancelledLate && settings.BillNoShows
            ? $"Check-in to {name} cancelled. It was less than {DisplayNames.Plural(settings.CancelHours, "hour")} before the class, so it will still be billed ({DisplayNames.Money(YourPrice(session.Group.Course, studentId, await DiscountsAsync(studentId)))})."
            : $"Check-in to {name} cancelled.");
    }

    public async Task<List<ClassDate>> GetForTeacherAsync(string teacherId)
    {
        var sessions = await PerClassSessions()
            .Where(s => s.Group!.TeacherId == teacherId &&
                        db.Memberships.Any(m => m.UserId == teacherId && m.OrganizationId == s.OrganizationId &&
                                                m.Role == MembershipRole.Teacher && m.Status == MembershipStatus.Active))
            .ToListAsync();
        DateOnly today = clock.SchoolToday();
        return await DatesAsync(sessions, today.AddDays(-DaysBack), today.AddDays(DaysAhead), null);
    }

    public async Task<List<ClassDate>> GetForOrganizationAsync(int organizationId)
    {
        var sessions = await PerClassSessions().Where(s => s.OrganizationId == organizationId).ToListAsync();
        DateOnly today = clock.SchoolToday();
        return await DatesAsync(sessions, today.AddDays(-DaysBack), today.AddDays(DaysAhead), null);
    }

    public async Task<ClassRoster?> GetRosterAsync(int organizationId, int sessionId, DateOnly date, string? teacherId)
    {
        ClassSession? session = await PerClassSessions()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.OrganizationId == organizationId && (teacherId == null || s.Group!.TeacherId == teacherId));
        if (session == null || date.DayOfWeek != session.Day)
        {
            return null;
        }

        ClassDate? classDate = (await DatesAsync([session], date, date, null)).FirstOrDefault();
        if (classDate == null)
        {
            return null;
        }
        var bookings = await db.Bookings.AsNoTracking().Include(b => b.Student)
            .Where(b => b.SessionId == sessionId && b.Date == date && (b.CancelledAt == null || b.CancelledLate))
            .ToListAsync();
        return new ClassRoster(classDate,
            bookings.OrderBy(b => b.CancelledAt != null).ThenBy(b => b.Student!.LastName).ThenBy(b => b.Student!.FirstName).ToList(),
            await SettingsAsync(organizationId), classDate.Starts <= clock.SchoolNow());
    }

    public async Task<ServiceResult> SetNoShowAsync(int organizationId, int bookingId, bool noShow, string? teacherId)
    {
        Booking? booking = await db.Bookings.Include(b => b.Student).Include(b => b.Session).ThenInclude(s => s!.Group)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.OrganizationId == organizationId && b.CancelledAt == null &&
                                      (teacherId == null || b.Session!.Group!.TeacherId == teacherId));
        if (booking == null)
        {
            return ServiceResult.Fail("That check-in doesn't exist or was cancelled.");
        }
        if (booking.Date.ToDateTime(booking.Session!.Starts) > clock.SchoolNow())
        {
            return ServiceResult.Fail("The class hasn't started yet: mark who didn't come once it has.");
        }
        // Once the month is billed, the charge is what counts: changing the check-in would no longer change it.
        if (booking.ChargeId != null)
        {
            return ServiceResult.Fail($"{DisplayNames.MonthName(booking.Date.FirstOfMonth())} is already billed. Ask an admin to adjust {booking.Student!.FullName}'s charge on the Payments tab.");
        }

        booking.NoShow = noShow;
        await db.SaveChangesAsync();
        return ServiceResult.Ok(noShow ? $"{booking.Student!.FullName} marked as not there." : $"{booking.Student!.FullName} marked as there.");
    }

    public async Task<IReadOnlyDictionary<int, PaymentSettings>> GetSettingsAsync(IEnumerable<int> organizationIds)
    {
        var ids = organizationIds.Distinct().ToList();
        var saved = await db.PaymentSettings.AsNoTracking().Where(s => ids.Contains(s.OrganizationId)).ToDictionaryAsync(s => s.OrganizationId);
        return ids.ToDictionary(id => id, id => saved.GetValueOrDefault(id) ?? new PaymentSettings { OrganizationId = id });
    }

    // ---- Helpers ----

    private Task<List<StudentDiscount>> DiscountsAsync(string studentId) =>
        db.StudentDiscounts.AsNoTracking().Include(d => d.Discount).Where(d => d.StudentId == studentId).ToListAsync();

    private static decimal YourPrice(Course course, string studentId, IEnumerable<StudentDiscount> discounts) =>
        course.Price - BillingService.ApplyDiscounts(course.Price,
            BillingService.Applicable(discounts.Where(d => d.OrganizationId == course.OrganizationId), studentId, course.Id, null)).Discount;

    private IQueryable<ClassSession> PerClassSessions() =>
        db.ClassSessions.AsNoTracking()
            .Include(s => s.Group).ThenInclude(g => g!.Course)
            .Include(s => s.Group).ThenInclude(g => g!.Teacher)
            .Include(s => s.Classroom)
            .Where(s => s.Group!.Course!.Pricing == CoursePricing.PerClass && s.Group.Course.Organization!.Status == OrganizationStatus.Active);

    // Every date from..to on which these weekly classes meet (inside the term), with places taken and the student's
    // own check-in.
    private async Task<List<ClassDate>> DatesAsync(IReadOnlyList<ClassSession> sessions, DateOnly from, DateOnly to, string? studentId)
    {
        if (sessions.Count == 0)
        {
            return [];
        }

        var organizationIds = sessions.Select(s => s.OrganizationId).Distinct().ToList();
        var organizations = await db.Organizations.AsNoTracking().Where(o => organizationIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
        var rules = await db.OrganizationRules.AsNoTracking().Where(r => organizationIds.Contains(r.OrganizationId)).ToDictionaryAsync(r => r.OrganizationId);
        var sessionIds = sessions.Select(s => s.Id).ToList();
        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => sessionIds.Contains(b.SessionId) && b.Date >= from && b.Date <= to)
            .ToListAsync();

        var result = new List<ClassDate>();
        foreach (ClassSession session in sessions)
        {
            for (DateOnly date = from; date <= to; date = date.AddDays(1))
            {
                if (date.DayOfWeek != session.Day || !InTerm(rules.GetValueOrDefault(session.OrganizationId), date))
                {
                    continue;
                }
                var these = bookings.Where(b => b.SessionId == session.Id && b.Date == date).ToList();
                result.Add(new ClassDate(session, organizations[session.OrganizationId], date, Places(session),
                    these.Count(b => b.CancelledAt == null),
                    studentId == null ? null : these.FirstOrDefault(b => b.StudentId == studentId && b.CancelledAt == null),
                    studentId != null && these.Any(b => b.StudentId == studentId && b.CancelledAt != null && b.CancelledLate)));
            }
        }
        return result.OrderBy(c => c.Date).ThenBy(c => c.Session.Starts).ThenBy(c => c.Session.Group!.Course!.Name).ToList();
    }

    // Places in one class: the group's places, or fewer if the room is smaller.
    private static int Places(ClassSession session) => Math.Min(session.Group!.Capacity, session.Classroom!.Capacity);

    private static bool InTerm(OrganizationRules? rules, DateOnly date) =>
        (rules?.TermStarts == null || date >= rules.TermStarts) && (rules?.TermEnds == null || date <= rules.TermEnds);

    private Task<bool> IsActiveStudentAsync(string studentId, int organizationId) =>
        db.Memberships.AnyAsync(m => m.UserId == studentId && m.OrganizationId == organizationId &&
                                     m.Role == MembershipRole.Student && m.Status == MembershipStatus.Active);

    private async Task<PaymentSettings> SettingsAsync(int organizationId) =>
        await db.PaymentSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == organizationId)
        ?? new PaymentSettings { OrganizationId = organizationId };
}
