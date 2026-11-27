using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Extensions;
using SecretariArt.Authorization;
using SecretariArt.Data;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

public class HomeController(
    ICurrentUserMemberships currentMemberships, SecretariArtDbContext db, IScheduleService schedule, TimeProvider clock) : Controller
{
    public async Task<IActionResult> Index()
    {
        string userId = User.UserId();
        IReadOnlyList<Membership> memberships = await currentMemberships.GetAsync();

        var adminOf = memberships
            .Where(m => m.Role == MembershipRole.Admin && m.Status == MembershipStatus.Active)
            .Select(m => m.OrganizationId)
            .ToList();

        // One grouped query for all administered organisations, rather than one per organisation.
        Dictionary<int, int> pendingJoinRequests = await db.Memberships
            .Where(m => adminOf.Contains(m.OrganizationId) && m.Status == MembershipStatus.Pending)
            .GroupBy(m => m.OrganizationId)
            .Select(g => new { OrganizationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.OrganizationId, g => g.Count);

        // Requests that haven't become an organisation yet (an approved one shows up as an Admin membership).
        List<Organization> requests = await db.Organizations.AsNoTracking()
            .Where(o => o.RequestedById == userId && o.Status != OrganizationStatus.Active)
            .OrderByDescending(o => o.RequestedAt)
            .ToListAsync();

        // Once someone asks again under the same name, the older rejection only confuses: keep the newest per name.
        requests = requests
            .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // The platform admin's work starts here, not with joining a school.
        int? platformRequestsWaiting = User.IsInRole(Policies.PlatformAdminRole)
            ? await db.Organizations.CountAsync(o => o.Status == OrganizationStatus.Pending)
            : null;

        // The requests themselves, for "Needs your attention" (the counts above drive the organisation cards).
        List<Membership> joinRequests = await db.Memberships.AsNoTracking().Include(m => m.User).Include(m => m.Organization)
            .Where(m => adminOf.Contains(m.OrganizationId) && m.Status == MembershipStatus.Pending)
            .OrderByDescending(m => m.RequestedAt).Take(10).ToListAsync();

        // "Coming up": today's classes, and payments due in the next two weeks or already overdue. In memory: SQLite (the
        // tests) can't compare decimals, and one person's charges are few.
        DateOnly today = clock.SchoolToday();
        UserRoles roles = await currentMemberships.GetRolesAsync();
        var todaysClasses = roles.IsTeacher || roles.IsStudent
            ? (await schedule.GetPersonalAsync(userId)).Where(p => p.Session.Day == today.DayOfWeek).OrderBy(p => p.Session.Starts).ToList()
            : [];
        var paymentsDue = (await db.Charges.AsNoTracking().Include(c => c.Payments).Include(c => c.Organization)
                .Where(c => c.StudentId == userId && c.CancelledAt == null && c.DueOn <= today.AddDays(PaymentDefaults.DueSoonDays)).ToListAsync())
            .Where(c => c.Balance > 0).OrderBy(c => c.DueOn).ToList();

        return View(new HomeViewModel
        {
            PlatformRequestsWaiting = platformRequestsWaiting,
            JoinRequests = joinRequests,
            Today = today,
            TodaysClasses = todaysClasses,
            PaymentsDue = paymentsDue,
            FirstName = User.FindFirstValue(ClaimTypes.GivenName) ?? "",
            Memberships = memberships,
            OrganizationRequests = requests,
            PendingJoinRequests = pendingJoinRequests,
        });
    }
}
