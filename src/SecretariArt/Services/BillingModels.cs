using SecretariArt.Models;

namespace SecretariArt.Services;

// The records BillingService takes and returns.

public record PaymentInput(decimal Amount, DateOnly PaidOn, PaymentMethod Method, string? Reference);

public record ChargeInput(string Description, decimal Amount, DateOnly DueOn);

// Classes checked in to this month, not billed yet: shown as "so far" until the month is billed.
public record ClassesSoFar(Course Course, int Classes, decimal Amount);

// One student's account at one organisation.
public record Statement(
    Organization Organization, ApplicationUser Student, IReadOnlyList<Charge> Charges, IReadOnlyList<StudentDiscount> Discounts,
    IReadOnlyList<Enrolment> Enrolments,
    IReadOnlyDictionary<int, Bundle> Bundled, IReadOnlyList<ClassesSoFar> SoFar, DateOnly Today)
{
    public decimal Owed => Charges.Sum(c => c.Balance);
    public decimal Overdue => Charges.Where(c => c.IsOverdue(Today)).Sum(c => c.Balance);
}

// What a price or discount change did to this month's unpaid fees.
public record RepriceResult(int Updated, int Removed, int Added, IReadOnlyList<string> WaitingForBundle)
{
    // " This month's unpaid fees were brought up to date: 2 changed, 3 removed, 1 added." or nothing.
    public string Describe()
    {
        var parts = new[] { (Updated, "changed"), (Removed, "removed"), (Added, "added") }
            .Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {p.Item2}").ToList();
        string waiting = WaitingForBundle.Count == 0 ? "" :
            $" {string.Join(", ", WaitingForBundle)} already paid part of this month's course fees, so {(WaitingForBundle.Count == 1 ? "their" : "each one's")} bundle price starts next month.";
        return (parts.Count == 0 ? "" : $" This month's unpaid fees were brought up to date: {string.Join(", ", parts)}.") + waiting;
    }
}

public enum BooksFilter { Open, Overdue, Paid, All }

public record BooksStudent(ApplicationUser Student, decimal Owed, decimal Overdue, DateOnly? NextDue, bool HasDiscount);

// The organisation's books for one view: the charges that match, and the totals that always show.
public record Books(
    IReadOnlyList<Charge> Charges, IReadOnlyList<BooksStudent> Students, decimal Overdue, decimal DueSoon,
    decimal CollectedThisMonth, DateOnly Today, int OverdueCount);

// Each charge billing makes has a key that is unique per organisation (a database index), so making charges twice
// (two requests at once, the hourly run) can't bill twice. These formats are stored: don't change them.
public static class ChargeKeys
{
    public static string Bundle(int bundleId, string studentId, DateOnly month) => $"B:{bundleId}:{studentId}:{month:yyyy-MM}";

    public static string Monthly(int courseId, string studentId, DateOnly month) => $"M:{courseId}:{studentId}:{month:yyyy-MM}";

    public static string PerClass(int courseId, string studentId, DateOnly month) => $"P:{courseId}:{studentId}:{month:yyyy-MM}";
}
