using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

public class HomeController(ICurrentUserMemberships currentMemberships, SecretariArtDbContext db) : Controller
{
    public async Task<IActionResult> Index()
    {
        string userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
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

        return View(new HomeViewModel
        {
            FirstName = User.FindFirstValue(ClaimTypes.GivenName) ?? "",
            Memberships = memberships,
            OrganizationRequests = requests,
            PendingJoinRequests = pendingJoinRequests,
        });
    }
}
