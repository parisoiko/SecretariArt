using Microsoft.EntityFrameworkCore;
using SecretariArt.Models;

namespace SecretariArt.Data;

public static class PaymentQueries
{
    // An organisation that hasn't saved payment settings uses the defaults.
    public static async Task<PaymentSettings> ForOrganizationAsync(this IQueryable<PaymentSettings> settings, int organizationId) =>
        await settings.AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == organizationId)
        ?? new PaymentSettings { OrganizationId = organizationId };

    // The organisation's pay-per-class courses, by id.
    public static Task<Dictionary<int, Course>> PerClassAsync(this IQueryable<Course> courses, int organizationId) =>
        courses.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.Pricing == CoursePricing.PerClass)
            .ToDictionaryAsync(c => c.Id);
}
