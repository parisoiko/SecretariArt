using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.ViewComponents;

public record AdminSidebarLink(string Key, string Controller, string Action, string Label, int Badge = 0);

public record AdminSidebarModel(Organization Organization, string Active, IReadOnlyList<(string Group, IReadOnlyList<AdminSidebarLink> Links)> Groups);

// The administration sections of one organisation, grouped the way the work goes: people, setting up, running it.
// Members carries the number of people waiting for approval, so it's seen from every admin page.
public class AdminSidebarViewComponent(SecretariArtDbContext db) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Organization organization, string active)
    {
        int waiting = await db.Memberships.CountAsync(m => m.OrganizationId == organization.Id && m.Status == MembershipStatus.Pending);
        return View(new AdminSidebarModel(organization, active,
        [
            ("People", [
                new("members", "Administration", "Members", "Members", waiting),
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
        ]));
    }
}
