using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.ViewComponents;

public record AdminSidebarLink(string Key, string Controller, string Action, string Label, int Badge = 0);

public record AdminSidebarModel(Organization Organization, string Active, IReadOnlyList<(string Group, IReadOnlyList<AdminSidebarLink> Links)> Groups);

// The administration sections of one organisation, grouped the way the work goes: people, setting up, running it.
// Views name their section with ViewData["AdminTab"] (a Key below).
public static class AdminSections
{
    public static readonly IReadOnlyList<(string Group, IReadOnlyList<AdminSidebarLink> Links)> Groups =
    [
        ("People", [
            new("members", "Administration", "Members", "Members"),
            new("enrolments", "Enrolments", "Index", "Enrolments"),
        ]),
        ("Set-up", [
            new("rules", "Rules", "Edit", "Rules"),
            new("classrooms", "Classrooms", "Index", "Classrooms"),
            new("courses", "Courses", "Index", "Courses"),
            new("bundles", "Bundles", "Index", "Bundles"),
        ]),
        ("Running", [
            new("schedule", "Schedule", "Index", "Schedule"),
            new("payments", "Payments", "Index", "Payments"),
        ]),
    ];

    // The section with this key; Members for anything else.
    public static AdminSidebarLink Find(string? key) =>
        Groups.SelectMany(g => g.Links).FirstOrDefault(l => l.Key == key) ?? Groups[0].Links[0];
}

// Members carries the number of people waiting for approval, so it's seen from every admin page.
public class AdminSidebarViewComponent(SecretariArtDbContext db) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Organization organization, string active)
    {
        int waiting = await db.Memberships.CountAsync(m => m.OrganizationId == organization.Id && m.Status == MembershipStatus.Pending);
        var groups = AdminSections.Groups
            .Select(g => (g.Group, (IReadOnlyList<AdminSidebarLink>)g.Links.Select(l => l.Key == "members" ? l with { Badge = waiting } : l).ToList()))
            .ToList();
        return View(new AdminSidebarModel(organization, active, groups));
    }
}
