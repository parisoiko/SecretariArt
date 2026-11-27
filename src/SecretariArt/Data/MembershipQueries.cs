using Microsoft.EntityFrameworkCore;
using SecretariArt.Models;

namespace SecretariArt.Data;

// The membership filters every service needs, so "who counts as a student here" is written once.
public static class MembershipQueries
{
    // Approved, in an organisation that is itself active: what lets someone see and do anything there.
    public static IQueryable<Membership> Live(this IQueryable<Membership> memberships) =>
        memberships.Where(m => m.Status == MembershipStatus.Active && m.Organization!.Status == OrganizationStatus.Active);

    // Approved with this role (whatever the organisation's own status: billing and rosters still apply to a
    // suspended one).
    public static IQueryable<Membership> Approved(this IQueryable<Membership> memberships, MembershipRole role) =>
        memberships.Where(m => m.Role == role && m.Status == MembershipStatus.Active);

    public static Task<bool> HasApprovedAsync(this IQueryable<Membership> memberships, string userId, int organizationId, MembershipRole role) =>
        memberships.Approved(role).AnyAsync(m => m.UserId == userId && m.OrganizationId == organizationId);
}
