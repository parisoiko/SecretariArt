namespace SecretariArt.Models;

// How a course is paid for. A bundle can have its own monthly price, which then replaces its courses' prices for the
// students who take it as a bundle.
public enum CoursePricing
{
    // No price yet: nothing is billed.
    NotSet,
    // A fixed fee each month of the term, whether the student comes or not. Billed automatically.
    Monthly,
    // A price for each class the student checked in to (and, if the organisation bills no-shows, didn't cancel in
    // time). Students book each class beforehand, which also keeps classes within their places.
    PerClass,
}

// The organisation's payment rules, edited on Payments → Settings.
public class PaymentSettings
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    // Monthly fees are due on this day of their month; per-class charges on this day of the following month.
    public int DueDay { get; set; } = PaymentDefaults.DueDay;

    // Students get a reminder this many days before a charge is due (0: no reminders).
    public int ReminderDays { get; set; } = PaymentDefaults.ReminderDays;

    // Per-class courses: a student who checked in but didn't come, or cancelled less than CancelHours before the class,
    // is billed for it.
    public bool BillNoShows { get; set; }
    public int CancelHours { get; set; } = PaymentDefaults.CancelHours;

    public DateTime UpdatedAt { get; set; }
}

public static class PaymentDefaults
{
    public const int DueDay = 5;
    public const int ReminderDays = 3;
    public const int CancelHours = 4;

    // How far ahead students can check in to per-class courses.
    public const int BookingDaysAhead = 28;
}

// A discount the organisation gives, e.g. "Siblings" 25%, "Scholarship" 50%.
public class Discount
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public string Name { get; set; } = "";
    public decimal Percent { get; set; }
}

// A discount one student gets: a named one, or a one-off with its own percentage and reason. It applies to all their
// charges at the organisation, or only to one course or one bundle.
public class StudentDiscount
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }

    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }

    public int? DiscountId { get; set; }
    public Discount? Discount { get; set; }

    // For a one-off discount (no DiscountId).
    public decimal? Percent { get; set; }
    public string? Reason { get; set; }

    public int? CourseId { get; set; }
    public Course? Course { get; set; }
    public int? BundleId { get; set; }
    public Bundle? Bundle { get; set; }

    public DateTime CreatedAt { get; set; }

    public decimal EffectivePercent => Discount?.Percent ?? Percent ?? 0;
    public string Label => Discount?.Name ?? Reason ?? "Discount";
}

// A check-in to one dated class of a per-class course ("Life drawing, Thursday 27 November, 18:00").
public class Booking
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }

    // Null once the weekly class has come off the timetable: the check-in stays, because it may still be billed.
    public int? SessionId { get; set; }
    public ClassSession? Session { get; set; }

    // The course it bills, kept here so billing works whatever happens to the class (milestone 5 fix). A plain id,
    // like Charge.CourseId: a check-in of a course deleted since is simply not billed.
    public int CourseId { get; set; }

    public DateOnly Date { get; set; }

    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }

    public DateTime BookedAt { get; set; }

    // Cancelled check-ins are kept: a late cancellation can still be billed.
    public DateTime? CancelledAt { get; set; }
    public bool CancelledLate { get; set; }

    // Set by the teacher or an admin when the student didn't come; null means they came (or nobody said otherwise).
    public bool NoShow { get; set; }

    // Charged in this monthly charge, once the month is billed.
    public int? ChargeId { get; set; }
}

public enum ChargeKind
{
    Monthly,
    PerClass,
    Bundle,
    Other,
}

// Something a student owes: a month of a course or bundle, a month of classes attended, or a one-off charge the admin
// adds (e.g. a registration fee). Amounts are kept as they were worked out, so a later price change doesn't rewrite
// the books.
public class Charge
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }

    public ChargeKind Kind { get; set; }

    // Makes billing safe to run again: one charge per student, course or bundle and month. Null for one-off charges.
    public string? Key { get; set; }

    public int? CourseId { get; set; }
    public int? BundleId { get; set; }

    // The month a monthly or per-class charge is for (its first day).
    public DateOnly? Month { get; set; }

    public string Description { get; set; } = "";
    public int? Classes { get; set; }

    public decimal BaseAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? DiscountNote { get; set; }
    public decimal Amount { get; set; }

    public DateOnly DueOn { get; set; }

    public DateTime CreatedAt { get; set; }

    // A waived charge stays in the books, crossed out, with the reason.
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    public DateTime? ReminderSentAt { get; set; }

    public List<Payment> Payments { get; set; } = [];

    public decimal Paid => Payments.Sum(p => p.Amount);
    public decimal Balance => CancelledAt != null ? 0 : Math.Max(0, Amount - Paid);
    public bool IsPaid => CancelledAt == null && Balance == 0;
    public bool IsOverdue(DateOnly today) => Balance > 0 && DueOn < today;
}

public enum PaymentMethod
{
    Cash,
    Card,
    BankTransfer,
    // Kept for paying in the app later (a provider such as Stripe); not offered yet.
    Online,
}

// Money received against a charge. Several part-payments can settle one charge.
public class Payment
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }

    public int ChargeId { get; set; }
    public Charge? Charge { get; set; }

    public decimal Amount { get; set; }
    public DateOnly PaidOn { get; set; }
    public PaymentMethod Method { get; set; }

    // A receipt number, bank reference or, later, the payment provider's id.
    public string? Reference { get; set; }

    public string? RecordedById { get; set; }
    public ApplicationUser? RecordedBy { get; set; }
    public DateTime RecordedAt { get; set; }
}
