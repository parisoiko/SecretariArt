using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

public record PaymentSettingsInput(int DueDay, int ReminderDays, bool BillNoShows, int CancelHours);

public record CoursePriceInput(int CourseId, CoursePricing Pricing, decimal Price);

public record BundlePriceInput(int BundleId, decimal? MonthlyPrice);

public record DiscountInput(string Name, decimal Percent);

// A discount for one student: a named one (DiscountId) or a one-off (Percent + Reason), on everything or on one
// course or bundle.
public record StudentDiscountInput(int? DiscountId, decimal? Percent, string? Reason, int? CourseId, int? BundleId);

public record PriceList(IReadOnlyList<Course> Courses, IReadOnlyList<Bundle> Bundles);

public interface IPaymentSetupService
{
    Task<PaymentSettings> GetSettingsAsync(int organizationId);
    Task<ServiceResult> SaveSettingsAsync(int organizationId, PaymentSettingsInput input);

    Task<PriceList> GetPricesAsync(int organizationId);
    Task<ServiceResult> SavePricesAsync(int organizationId, IReadOnlyCollection<CoursePriceInput> courses, IReadOnlyCollection<BundlePriceInput> bundles);

    Task<List<Discount>> GetDiscountsAsync(int organizationId);
    Task<ServiceResult> AddDiscountAsync(int organizationId, DiscountInput input);
    Task<ServiceResult> DeleteDiscountAsync(int organizationId, int discountId);

    Task<ServiceResult> GiveDiscountAsync(int organizationId, string studentId, StudentDiscountInput input);
    Task<ServiceResult> RemoveStudentDiscountAsync(int organizationId, int studentDiscountId);
}

public class PaymentSetupService(SecretariArtDbContext db, IMembershipService memberships, IBillingService billing, TimeProvider clock)
    : IPaymentSetupService
{
    public const decimal MaxPrice = 100_000m;

    public async Task<PaymentSettings> GetSettingsAsync(int organizationId) =>
        await db.PaymentSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == organizationId)
        ?? new PaymentSettings { OrganizationId = organizationId };

    public async Task<ServiceResult> SaveSettingsAsync(int organizationId, PaymentSettingsInput input)
    {
        PaymentSettings? settings = await db.PaymentSettings.FirstOrDefaultAsync(s => s.OrganizationId == organizationId);
        if (settings == null)
        {
            settings = new PaymentSettings { OrganizationId = organizationId };
            db.PaymentSettings.Add(settings);
        }
        settings.DueDay = input.DueDay;
        settings.ReminderDays = input.ReminderDays;
        settings.BillNoShows = input.BillNoShows;
        settings.CancelHours = input.CancelHours;
        settings.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        return ServiceResult.Ok("Payment settings saved. New charges use them; charges already made keep their due dates.");
    }

    public async Task<PriceList> GetPricesAsync(int organizationId) => new(
        await db.Courses.AsNoTracking().Where(c => c.OrganizationId == organizationId).OrderBy(c => c.Name).ToListAsync(),
        await db.Bundles.AsNoTracking().Include(b => b.Courses).ThenInclude(l => l.Course)
            .Where(b => b.OrganizationId == organizationId).OrderBy(b => b.Name).ToListAsync());

    public async Task<ServiceResult> SavePricesAsync(
        int organizationId, IReadOnlyCollection<CoursePriceInput> courses, IReadOnlyCollection<BundlePriceInput> bundles)
    {
        var allCourses = await db.Courses.Where(c => c.OrganizationId == organizationId).ToDictionaryAsync(c => c.Id);
        var allBundles = await db.Bundles.Where(b => b.OrganizationId == organizationId).ToDictionaryAsync(b => b.Id);
        if (courses.Any(c => !allCourses.ContainsKey(c.CourseId)) || bundles.Any(b => !allBundles.ContainsKey(b.BundleId)))
        {
            return ServiceResult.Fail("Some of those courses or bundles no longer exist. Reload the page and try again.");
        }

        // Every problem at once, one per line, in words that fit the value (QA, P3); nothing is saved until all are fixed.
        var problems = courses
            .Where(c => c.Pricing != CoursePricing.NotSet)
            .Select(c => PriceProblem(allCourses[c.CourseId].Name, c.Price, emptyAllowed: false))
            .Concat(bundles.Where(b => b.MonthlyPrice != null).Select(b => PriceProblem($"{allBundles[b.BundleId].Name} (bundle)", b.MonthlyPrice!.Value, emptyAllowed: true)))
            .OfType<string>().ToList();
        if (problems.Count > 0)
        {
            return ServiceResult.Fail(string.Join("\n", problems));
        }

        foreach (CoursePriceInput input in courses)
        {
            Course course = allCourses[input.CourseId];
            course.Pricing = input.Pricing;
            course.Price = input.Pricing == CoursePricing.NotSet ? 0 : input.Price;
        }
        foreach (BundlePriceInput input in bundles)
        {
            allBundles[input.BundleId].MonthlyPrice = input.MonthlyPrice;
        }
        await db.SaveChangesAsync();

        // This month's unpaid fees follow the new prices; anything paid or from earlier months stays as it was.
        return ServiceResult.Ok("Prices saved." + (await billing.RepriceAsync(organizationId, null)).Describe());
    }

    private static string? PriceProblem(string name, decimal price, bool emptyAllowed) => price switch
    {
        < 0 => $"{name}: a price can't be negative.",
        0 => emptyAllowed ? $"{name}: enter a price above €0, or leave it empty to use its courses' prices." : $"{name}: enter the price, or choose “No price yet”.",
        > MaxPrice => $"{name}: the most a price can be is {DisplayNames.Money(MaxPrice)}.",
        _ when price != Math.Round(price, 2) => $"{name}: use whole cents, e.g. 12.50.",
        _ => null,
    };

    public Task<List<Discount>> GetDiscountsAsync(int organizationId) =>
        db.Discounts.AsNoTracking().Where(d => d.OrganizationId == organizationId).OrderBy(d => d.Name).ToListAsync();

    public async Task<ServiceResult> AddDiscountAsync(int organizationId, DiscountInput input)
    {
        string name = input.Name.Trim();
        var existing = await db.Discounts.Where(d => d.OrganizationId == organizationId).Select(d => d.Name).ToListAsync();
        if (existing.Any(n => TextMatch.SameName(n, name)))
        {
            return ServiceResult.Fail($"There's already a discount called {name}.");
        }
        if (input.Percent is <= 0 or > 100)
        {
            return ServiceResult.Fail("Enter a percentage between 1 and 100.");
        }

        db.Discounts.Add(new Discount { OrganizationId = organizationId, Name = name, Percent = Math.Round(input.Percent, 2) });
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{name} ({input.Percent:0.##}%) added. Give it to students from their page in the books.");
    }

    public async Task<ServiceResult> DeleteDiscountAsync(int organizationId, int discountId)
    {
        Discount? discount = await db.Discounts.FirstOrDefaultAsync(d => d.Id == discountId && d.OrganizationId == organizationId);
        if (discount == null)
        {
            return ServiceResult.Fail("That discount doesn't exist any more.");
        }

        // Students who had it lose it from now on; charges already made keep the amount they were given.
        var given = await db.StudentDiscounts.Where(d => d.DiscountId == discountId).ToListAsync();
        db.StudentDiscounts.RemoveRange(given);
        db.Discounts.Remove(discount);
        await db.SaveChangesAsync();
        RepriceResult repriced = await billing.RepriceAsync(organizationId, null);
        return ServiceResult.Ok($"{discount.Name} deleted." +
            (given.Count > 0 ? $" {DisplayNames.Plural(given.Count, "student")} no longer {(given.Count == 1 ? "has" : "have")} it." : "") +
            repriced.Describe());
    }

    public async Task<ServiceResult> GiveDiscountAsync(int organizationId, string studentId, StudentDiscountInput input)
    {
        if (!await memberships.HasActiveRoleAsync(studentId, organizationId, MembershipRole.Student))
        {
            return ServiceResult.Fail("That person isn't an approved student of this organisation.");
        }

        Discount? named = null;
        if (input.DiscountId != null)
        {
            named = await db.Discounts.FirstOrDefaultAsync(d => d.Id == input.DiscountId && d.OrganizationId == organizationId);
            if (named == null)
            {
                return ServiceResult.Fail("That discount doesn't exist any more.");
            }
        }
        else if (input.Percent is not (> 0 and <= 100) || string.IsNullOrWhiteSpace(input.Reason))
        {
            return ServiceResult.Fail("Choose a discount, or enter a percentage (1–100) and the reason for it.");
        }
        if (input.CourseId != null && input.BundleId != null)
        {
            return ServiceResult.Fail("Choose either a course or a bundle, not both.");
        }

        string scope = "everything";
        if (input.CourseId != null)
        {
            scope = await db.Courses.Where(c => c.Id == input.CourseId && c.OrganizationId == organizationId).Select(c => c.Name).FirstOrDefaultAsync()
                ?? "";
        }
        else if (input.BundleId != null)
        {
            scope = await db.Bundles.Where(b => b.Id == input.BundleId && b.OrganizationId == organizationId).Select(b => b.Name).FirstOrDefaultAsync()
                ?? "";
        }
        if (scope == "")
        {
            return ServiceResult.Fail("That course or bundle doesn't exist any more.");
        }

        // The same named discount twice on the same thing would apply twice (QA, P5).
        if (named != null && await db.StudentDiscounts.AnyAsync(d => d.StudentId == studentId && d.OrganizationId == organizationId &&
                                                                    d.DiscountId == named.Id && d.CourseId == input.CourseId && d.BundleId == input.BundleId))
        {
            string name = await db.Users.Where(u => u.Id == studentId).Select(u => u.FirstName).FirstAsync();
            return ServiceResult.Fail($"{name} already has {named.Name} on {scope}.");
        }

        var discount = new StudentDiscount
        {
            OrganizationId = organizationId, StudentId = studentId, DiscountId = named?.Id,
            Percent = named == null ? Math.Round(input.Percent!.Value, 2) : null,
            Reason = named == null ? input.Reason!.Trim() : null,
            CourseId = input.CourseId, BundleId = input.BundleId, CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.StudentDiscounts.Add(discount);
        await db.SaveChangesAsync();

        RepriceResult repriced = await billing.RepriceAsync(organizationId, studentId);
        string label = named != null ? $"{named.Name} ({named.Percent:0.##}%)" : $"{discount.Percent:0.##}% ({discount.Reason})";
        return ServiceResult.Ok($"Discount {label} given, on {scope}." +
            repriced.Describe());
    }

    public async Task<ServiceResult> RemoveStudentDiscountAsync(int organizationId, int studentDiscountId)
    {
        StudentDiscount? discount = await db.StudentDiscounts.Include(d => d.Discount)
            .FirstOrDefaultAsync(d => d.Id == studentDiscountId && d.OrganizationId == organizationId);
        if (discount == null)
        {
            return ServiceResult.Fail("That discount was already removed.");
        }

        db.StudentDiscounts.Remove(discount);
        await db.SaveChangesAsync();
        RepriceResult repriced = await billing.RepriceAsync(organizationId, discount.StudentId);
        return ServiceResult.Ok($"Discount {discount.Label} removed." +
            repriced.Describe());
    }
}
