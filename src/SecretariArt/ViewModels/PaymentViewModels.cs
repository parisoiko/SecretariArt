using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using SecretariArt.Models;
using SecretariArt.Services;

namespace SecretariArt.ViewModels;

// The admin's Payments tab: the books.
public class BooksViewModel
{
    public required Books Books { get; init; }
    public BooksFilter Filter { get; init; }
    public string? Search { get; init; }

    // "charges" (one line per charge) or "students" (one line per student, with what they owe).
    public string View { get; init; } = "charges";
}

// One student's account, with everything the admin can do on it.
public class StatementViewModel
{
    public required Statement Statement { get; init; }
    public required IReadOnlyList<Discount> NamedDiscounts { get; init; }
    public required IReadOnlyList<Course> Courses { get; init; }
    public required IReadOnlyList<Bundle> Bundles { get; init; }
    public ChargeFormViewModel NewCharge { get; set; } = new();
    public GiveDiscountViewModel Discount { get; set; } = new();

    // After a refused form: which one ("charge", "discount", "waive-12"), why, and what was typed, so it opens
    // again with the problem next to it (QA, P6).
    public string? FormKey { get; set; }
    public string? FormError { get; set; }
    public string? WaiveReason { get; set; }

    // What a course costs this student: through their bundle, or the course's own price.
    public string PriceOf(Enrolment enrolment) =>
        Statement.Bundled.TryGetValue(enrolment.CourseId, out Bundle? bundle)
            ? $"in {bundle.Name} ({DisplayNames.Money(bundle.MonthlyPrice!.Value)} a month)"
            : enrolment.Course!.Pricing switch
            {
                CoursePricing.Monthly => $"{DisplayNames.Money(enrolment.Course.Price)} a month",
                CoursePricing.PerClass => $"{DisplayNames.Money(enrolment.Course.Price)} a class attended",
                _ => "no price yet",
            };
}

// One charge on a student's page, with its payments and what can be done to it. ReturnUrl brings the admin back to it.
public record ChargeCardModel(Charge Charge, StatementViewModel Page, string ReturnUrl)
{
    // The waive form was just refused: open it again with the reason typed.
    public bool WaiveRefused => Page.FormKey == $"waive-{Charge.Id}";
}

public class PaymentFormViewModel
{
    [Required(ErrorMessage = "Enter the amount received.")]
    [Range(0.01, 100000, ErrorMessage = "Enter the amount received.")]
    [Display(Name = "Amount (€)")]
    public decimal? Amount { get; set; }

    [Required(ErrorMessage = "Enter the date it was paid.")]
    [Display(Name = "Paid on")]
    public DateOnly? PaidOn { get; set; }

    [Required(ErrorMessage = "Choose how it was paid.")]
    [Display(Name = "Paid by")]
    public PaymentMethod? Method { get; set; }

    [StringLength(100)]
    [Display(Name = "Receipt or reference")]
    public string? Reference { get; set; }

    // Where to go back to (the student's page or the books).
    [ValidateNever] public string? ReturnUrl { get; set; }

    public PaymentInput ToInput() => new(Amount!.Value, PaidOn!.Value, Method!.Value, Reference);
}

public class ChargeFormViewModel
{
    [Required(ErrorMessage = "Say what the charge is for.")]
    [StringLength(200)]
    [Display(Name = "What for")]
    public string Description { get; set; } = "";

    [Required(ErrorMessage = "Enter the amount.")]
    [Range(0.01, 100000, ErrorMessage = "Enter an amount between €0.01 and €100,000.")]
    [Display(Name = "Amount (€)")]
    public decimal? Amount { get; set; }

    [Required(ErrorMessage = "Enter the due date.")]
    [Display(Name = "Due on")]
    public DateOnly? DueOn { get; set; }

    public ChargeInput ToInput() => new(Description, Amount!.Value, DueOn!.Value);
}

public class EditChargeViewModel
{
    [Required(ErrorMessage = "Enter the amount.")]
    [Display(Name = "Amount (€)")]
    public decimal? Amount { get; set; }

    [Required(ErrorMessage = "Enter the due date.")]
    [Display(Name = "Due on")]
    public DateOnly? DueOn { get; set; }
}

public class WaiveViewModel
{
    [Required(ErrorMessage = "Say why it's waived: the student sees it.")]
    [StringLength(RejectionReasons.MaxLength)]
    public string Reason { get; set; } = "";
}

public class GiveDiscountViewModel
{
    // A named discount, or "custom" with Percent and Reason.
    [Required(ErrorMessage = "Choose a discount.")]
    public string Kind { get; set; } = "";

    [Display(Name = "Percentage")]
    public decimal? Percent { get; set; }

    [StringLength(StudentDiscount.ReasonMaxLength)]
    public string? Reason { get; set; }

    // "all", "course-5" or "bundle-3".
    [Required(ErrorMessage = "Choose what it applies to.")]
    public string Scope { get; set; } = "all";

    public StudentDiscountInput ToInput() => new(
        int.TryParse(Kind, out int id) ? id : null,
        Kind == "custom" ? Percent : null,
        Kind == "custom" ? Reason : null,
        Scope.StartsWith("course-") && int.TryParse(Scope["course-".Length..], out int course) ? course : null,
        Scope.StartsWith("bundle-") && int.TryParse(Scope["bundle-".Length..], out int bundle) ? bundle : null);
}

public class PricesViewModel
{
    public List<CoursePriceRow> Courses { get; set; } = [];
    public List<BundlePriceRow> Bundles { get; set; } = [];

    public static PricesViewModel From(PriceList prices) => new()
    {
        Courses = prices.Courses.Select(c => new CoursePriceRow
        {
            Id = c.Id, Name = c.Name, Pricing = c.Pricing, Price = c.Pricing == CoursePricing.NotSet ? null : c.Price,
        }).ToList(),
        Bundles = prices.Bundles.Select(b => new BundlePriceRow
        {
            Id = b.Id, Name = b.Name, MonthlyPrice = b.MonthlyPrice,
            CourseNames = string.Join(", ", b.Courses.Select(l => l.Course!.Name).Order()),
        }).ToList(),
    };
}

public class CoursePriceRow
{
    public int Id { get; set; }
    [ValidateNever] public string Name { get; set; } = "";
    public CoursePricing Pricing { get; set; }
    public decimal? Price { get; set; }
}

public class BundlePriceRow
{
    public int Id { get; set; }
    [ValidateNever] public string Name { get; set; } = "";
    [ValidateNever] public string CourseNames { get; set; } = "";
    public decimal? MonthlyPrice { get; set; }
}

public class PaymentSettingsViewModel
{
    [Required(ErrorMessage = "Enter the day of the month.")]
    [Range(1, 28, ErrorMessage = "Choose a day from 1 to 28, so it exists in every month.")]
    [Display(Name = "Fees are due on day")]
    public int? DueDay { get; set; }

    [Required(ErrorMessage = "Enter the number of days (0 for no reminders).")]
    [Range(0, 30, ErrorMessage = "Enter 0 to 30 days.")]
    [Display(Name = "Remind students this many days before")]
    public int? ReminderDays { get; set; }

    [Display(Name = "Bill students who check in but don't come")]
    public bool BillNoShows { get; set; }

    [Required(ErrorMessage = "Enter the number of hours.")]
    [Range(0, 72, ErrorMessage = "Enter 0 to 72 hours.")]
    [Display(Name = "Free cancellation until this many hours before the class")]
    public int? CancelHours { get; set; }

    [ValidateNever] public IReadOnlyList<Discount> Discounts { get; set; } = [];

    public static PaymentSettingsViewModel From(PaymentSettings settings, IReadOnlyList<Discount> discounts) => new()
    {
        DueDay = settings.DueDay, ReminderDays = settings.ReminderDays, BillNoShows = settings.BillNoShows,
        CancelHours = settings.CancelHours, Discounts = discounts,
    };

    public PaymentSettingsInput ToInput() => new(DueDay!.Value, ReminderDays!.Value, BillNoShows, CancelHours!.Value);
}

public class DiscountFormViewModel
{
    [Required(ErrorMessage = "Enter a name, e.g. Siblings.")]
    [StringLength(100)]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Enter the percentage.")]
    [Range(1, 100, ErrorMessage = "Enter a percentage between 1 and 100.")]
    [Display(Name = "Percentage")]
    public decimal? Percent { get; set; }

    public DiscountInput ToInput() => new(Name, Percent!.Value);
}

// Per-class classes around today, for the Check-ins page (admin) or tab (teacher).
public class CheckInsViewModel
{
    public required IReadOnlyList<ClassDate> Classes { get; init; }
    public required DateOnly Today { get; init; }
}

public class RosterViewModel
{
    public required ClassRoster Roster { get; init; }
    public required bool AsAdmin { get; init; }
}

// A student's own Payments page, across organisations.
public class MyPaymentsViewModel
{
    public required IReadOnlyList<Statement> Statements { get; init; }
    public required DateOnly Today { get; init; }
}

public static class PaymentDisplay
{
    public static string Display(this PaymentMethod method) => method switch
    {
        PaymentMethod.BankTransfer => "Bank transfer",
        _ => method.ToString(),
    };

    public static string Display(this CoursePricing pricing) => pricing switch
    {
        CoursePricing.Monthly => "Monthly fee",
        CoursePricing.PerClass => "Per class attended",
        _ => "No price yet",
    };

    public static readonly PaymentMethod[] DeskMethods = [PaymentMethod.Cash, PaymentMethod.Card, PaymentMethod.BankTransfer];

    // The words and colour for a charge's state; never colour alone.
    public static (string Text, string Css) State(Charge charge, DateOnly today) =>
        charge.CancelledAt != null ? ("Waived", "status-waived")
        : charge.IsPaid ? ("Paid", "status-active")
        : charge.IsOverdue(today) ? ($"Overdue since {DisplayNames.ShortDate(charge.DueOn)}", "status-rejected")
        : charge.Paid > 0 ? ($"Part paid, due {DisplayNames.ShortDate(charge.DueOn)}", "status-pending")
        : ($"Due {DisplayNames.ShortDate(charge.DueOn)}", "status-pending");
}

// A list of dated per-class classes with how many checked in, each linking to its roster (admin or teacher route).
public record ClassDatesModel(IReadOnlyList<ClassDate> Classes, Func<ClassDate, string> Link);

// The "Record payment" form for one charge, on the books or a student's page.
public record RecordPaymentModel(Charge Charge, DateOnly Today, string? ReturnUrl);
