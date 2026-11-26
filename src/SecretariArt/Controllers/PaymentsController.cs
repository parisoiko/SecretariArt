using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SecretariArt.Extensions;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

// The organisation's payment books: charges, payments, discounts, prices, settings and class check-ins.
[Route("admin/{organizationId:int}/payments")]
public class PaymentsController(
    IOrganizationService organizations, IBillingService billing, IPaymentSetupService setup, IBookingService bookings, TimeProvider clock)
    : OrganizationAdminController(organizations)
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ---- Books ----

    [HttpGet("")]
    public async Task<IActionResult> Index(BooksFilter filter = BooksFilter.Open, string? search = null, string view = "charges") =>
        View(new BooksViewModel
        {
            Books = await billing.GetBooksAsync(OrganizationId, filter, search), Filter = filter, Search = search,
            View = view == "students" ? "students" : "charges",
        });

    [HttpGet("students/{studentId}")]
    public Task<IActionResult> Student(string studentId) => StudentPageAsync(studentId);

    // The student's page; after a refused form, shown again with what was typed and the problem next to it.
    private async Task<IActionResult> StudentPageAsync(string studentId, Action<StatementViewModel>? refused = null)
    {
        Statement? statement = await billing.GetStatementAsync(OrganizationId, studentId);
        if (statement == null)
        {
            return NotFound();
        }
        PriceList prices = await setup.GetPricesAsync(OrganizationId);
        var model = new StatementViewModel
        {
            Statement = statement, NamedDiscounts = await setup.GetDiscountsAsync(OrganizationId),
            Courses = prices.Courses, Bundles = prices.Bundles,
            NewCharge = new ChargeFormViewModel { DueOn = statement.Today.AddDays(BillingService.LateChargeDays) },
        };
        refused?.Invoke(model);
        return View(nameof(Student), model);
    }

    [HttpPost("charges/{chargeId:int}/payments")]
    public async Task<IActionResult> RecordPayment(int chargeId, PaymentFormViewModel model)
    {
        ServiceResult result = ModelState.IsValid
            ? await billing.RecordPaymentAsync(OrganizationId, chargeId, model.ToInput(), UserId)
            : ServiceResult.Fail(FirstError());
        return await BackToChargeAsync(result, chargeId, model.ReturnUrl);
    }

    [HttpPost("payments/{paymentId:int}/delete")]
    public async Task<IActionResult> DeletePayment(int paymentId, string studentId) =>
        ReportAndRedirect(await billing.DeletePaymentAsync(OrganizationId, paymentId), nameof(Student), new { studentId });

    [HttpPost("charges/{chargeId:int}")]
    public async Task<IActionResult> UpdateCharge(int chargeId, EditChargeViewModel model) =>
        await BackToChargeAsync(ModelState.IsValid
            ? await billing.UpdateChargeAsync(OrganizationId, chargeId, model.Amount!.Value, model.DueOn!.Value)
            : ServiceResult.Fail(FirstError()), chargeId, null);

    [HttpPost("charges/{chargeId:int}/waive")]
    public async Task<IActionResult> Waive(int chargeId, WaiveViewModel model)
    {
        ServiceResult result = ModelState.IsValid
            ? await billing.WaiveChargeAsync(OrganizationId, chargeId, model.Reason)
            : ServiceResult.Fail(FirstError());
        if (!result.Succeeded && await billing.FindChargeAsync(OrganizationId, chargeId) is Charge charge)
        {
            return await StudentPageAsync(charge.StudentId, page =>
                (page.FormKey, page.FormError, page.WaiveReason) = ($"waive-{chargeId}", result.Error, model.Reason));
        }
        return await BackToChargeAsync(result, chargeId, null);
    }

    [HttpPost("students/{studentId}/charges")]
    public async Task<IActionResult> AddCharge(string studentId, [Bind(Prefix = "NewCharge")] ChargeFormViewModel model)
    {
        // Every missing field is shown at once, next to the field (QA, P6).
        ServiceResult result = ModelState.IsValid
            ? await billing.AddChargeAsync(OrganizationId, studentId, model.ToInput())
            : ServiceResult.Fail("");
        return result.Succeeded
            ? ReportAndRedirect(result, nameof(Student), new { studentId })
            : await StudentPageAsync(studentId, page => (page.NewCharge, page.FormKey, page.FormError) = (model, "charge", result.Error));
    }

    [HttpPost("students/{studentId}/discounts")]
    public async Task<IActionResult> GiveDiscount(string studentId, GiveDiscountViewModel model)
    {
        ServiceResult result = ModelState.IsValid
            ? await setup.GiveDiscountAsync(OrganizationId, studentId, model.ToInput())
            : ServiceResult.Fail(FirstError());
        return result.Succeeded
            ? ReportAndRedirect(result, nameof(Student), new { studentId })
            : await StudentPageAsync(studentId, page => (page.Discount, page.FormKey, page.FormError) = (model, "discount", result.Error));
    }

    [HttpPost("student-discounts/{studentDiscountId:int}/remove")]
    public async Task<IActionResult> RemoveDiscount(int studentDiscountId, string studentId) =>
        ReportAndRedirect(await setup.RemoveStudentDiscountAsync(OrganizationId, studentDiscountId), nameof(Student), new { studentId });

    // ---- Prices ----

    [HttpGet("prices")]
    public async Task<IActionResult> Prices() => View(PricesViewModel.From(await setup.GetPricesAsync(OrganizationId)));

    [HttpPost("prices")]
    public async Task<IActionResult> Prices(PricesViewModel model)
    {
        // An amount that isn't a number at all is named with its course; otherwise the service checks every price.
        PricesViewModel shown = PricesViewModel.From(await setup.GetPricesAsync(OrganizationId));
        var problems = ModelState.Where(e => e.Value!.Errors.Count > 0).Select(e =>
        {
            string name = e.Key.StartsWith("Courses[")
                ? shown.Courses.FirstOrDefault(c => c.Id == model.Courses.ElementAtOrDefault(Index(e.Key))?.Id)?.Name ?? "A course"
                : $"{shown.Bundles.FirstOrDefault(b => b.Id == model.Bundles.ElementAtOrDefault(Index(e.Key))?.Id)?.Name ?? "A bundle"} (bundle)";
            // The reader's own words when it has something specific to say ("“1.250” could mean 1250 or 1.25…").
            string said = e.Value!.Errors[0].ErrorMessage;
            return said.StartsWith('“') ? $"{name}: {said}" : $"{name}: “{e.Value.AttemptedValue}” isn't an amount. Write it like 45 or 12.50.";
        }).ToList();
        if (problems.Count == 0)
        {
            ServiceResult result = await setup.SavePricesAsync(OrganizationId,
                model.Courses.Select(c => new CoursePriceInput(c.Id, c.Pricing, c.Price ?? 0)).ToList(),
                model.Bundles.Select(b => new BundlePriceInput(b.Id, b.MonthlyPrice)).ToList());
            if (result.Succeeded)
            {
                return ReportAndRedirect(result, nameof(Prices));
            }
            problems = result.Error!.Split('\n').ToList();
        }

        // Keep what was typed (QA, P3), even what couldn't be read as an amount (QA, P13), and list every problem.
        ViewData["Typed"] = ModelState.Where(e => e.Value!.Errors.Count > 0 && Index(e.Key) >= 0).ToDictionary(
            e => e.Key.StartsWith("Courses[") ? $"course-{model.Courses.ElementAtOrDefault(Index(e.Key))?.Id}" : $"bundle-{model.Bundles.ElementAtOrDefault(Index(e.Key))?.Id}",
            e => e.Value!.AttemptedValue);
        foreach (CoursePriceRow row in shown.Courses)
        {
            if (model.Courses.FirstOrDefault(c => c.Id == row.Id) is CoursePriceRow typed)
            {
                (row.Pricing, row.Price) = (typed.Pricing, typed.Price);
            }
        }
        foreach (BundlePriceRow row in shown.Bundles)
        {
            if (model.Bundles.FirstOrDefault(b => b.Id == row.Id) is BundlePriceRow typed)
            {
                row.MonthlyPrice = typed.MonthlyPrice;
            }
        }
        ModelState.Clear();
        problems.ForEach(p => ModelState.AddModelError("", p));
        return View(shown);

        static int Index(string key) =>
            key.IndexOf('[') is int open and >= 0 && key.IndexOf(']') > open && int.TryParse(key[(open + 1)..key.IndexOf(']')], out int i) ? i : -1;
    }

    // ---- Settings and named discounts ----

    [HttpGet("settings")]
    public async Task<IActionResult> Settings() =>
        View(PaymentSettingsViewModel.From(await setup.GetSettingsAsync(OrganizationId), await setup.GetDiscountsAsync(OrganizationId)));

    [HttpPost("settings")]
    public async Task<IActionResult> Settings(PaymentSettingsViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.Discounts = await setup.GetDiscountsAsync(OrganizationId);
            return View(model);
        }
        return ReportAndRedirect(await setup.SaveSettingsAsync(OrganizationId, model.ToInput()), nameof(Settings));
    }

    [HttpPost("discounts")]
    public async Task<IActionResult> AddDiscount(DiscountFormViewModel model) =>
        ReportAndRedirect(ModelState.IsValid
            ? await setup.AddDiscountAsync(OrganizationId, model.ToInput())
            : ServiceResult.Fail(FirstError()), nameof(Settings));

    [HttpPost("discounts/{discountId:int}/delete")]
    public async Task<IActionResult> DeleteDiscount(int discountId) =>
        ReportAndRedirect(await setup.DeleteDiscountAsync(OrganizationId, discountId), nameof(Settings));

    // ---- Check-ins (per-class courses) ----

    [HttpGet("check-ins")]
    public async Task<IActionResult> CheckIns() =>
        View(new CheckInsViewModel { Classes = await bookings.GetForOrganizationAsync(OrganizationId), Today = clock.SchoolToday() });

    [HttpGet("check-ins/{sessionId:int}/{date}")]
    public async Task<IActionResult> Roster(int sessionId, DateOnly date)
    {
        ClassRoster? roster = await bookings.GetRosterAsync(OrganizationId, sessionId, date, teacherId: null);
        return roster == null ? NotFound() : View("~/Views/Shared/Roster.cshtml", new RosterViewModel { Roster = roster, AsAdmin = true });
    }

    [HttpPost("check-ins/bookings/{bookingId:int}")]
    public async Task<IActionResult> SetNoShow(int bookingId, bool noShow, int sessionId, DateOnly date) =>
        ReportAndRedirect(await bookings.SetNoShowAsync(OrganizationId, bookingId, noShow, teacherId: null),
            nameof(Roster), new { sessionId, date = date.ToString("yyyy-MM-dd") });

    // ---- Helpers ----

    // After changing a charge, back to where the admin was: the books, or the student's page.
    private async Task<IActionResult> BackToChargeAsync(ServiceResult result, int chargeId, string? returnUrl)
    {
        this.Report(result);
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }
        var charge = await billing.FindChargeAsync(OrganizationId, chargeId);
        return charge == null
            ? RedirectToAction(nameof(Index), OrganizationRoute())
            : RedirectToAction(nameof(Student), OrganizationRoute(new { studentId = charge.StudentId }));
    }

    private string FirstError() =>
        ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault(m => !string.IsNullOrEmpty(m))
        ?? "Check the form and try again.";
}
