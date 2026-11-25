using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

public interface IMembershipService
{
    Task<Organization?> FindByInviteCodeAsync(string code);
    Task<ServiceResult> JoinAsync(string userId, string inviteCode, MembershipRole role);
    Task<List<Membership>> GetForUserAsync(string userId);
    Task<List<Membership>> GetForOrganizationAsync(int organizationId);
    Task<ServiceResult> DecideAsync(int organizationId, int membershipId, bool approve);
    Task<bool> IsActiveAdminAsync(string userId, int organizationId);
}

public class MembershipService(SecretariArtDbContext db, TimeProvider clock) : IMembershipService
{
    public Task<Organization?> FindByInviteCodeAsync(string code)
    {
        string normalized = InviteCodeGenerator.Normalize(code);
        return db.Organizations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.InviteCode == normalized && o.Status == OrganizationStatus.Active);
    }

    public async Task<ServiceResult> JoinAsync(string userId, string inviteCode, MembershipRole role)
    {
        // Admins are appointed, never self-requested.
        if (role == MembershipRole.Admin)
        {
            return ServiceResult.Fail("You can join as a student or a teacher.");
        }

        Organization? organization = await FindByInviteCodeAsync(inviteCode);
        if (organization == null)
        {
            return ServiceResult.Fail("That invite code doesn't match any organisation. Check it with the organisation.");
        }

        Membership? existing = await db.Memberships.FirstOrDefaultAsync(m =>
            m.UserId == userId && m.OrganizationId == organization.Id && m.Role == role);

        string roleName = role.ToString().ToLowerInvariant();
        switch (existing?.Status)
        {
            case MembershipStatus.Active:
                return ServiceResult.Fail($"You're already a {roleName} at {organization.Name}.");
            case MembershipStatus.Pending:
                return ServiceResult.Fail($"Your request to join {organization.Name} as a {roleName} is already waiting for approval.");
            case MembershipStatus.Rejected:
                // Asking again re-opens the old request instead of adding a duplicate.
                existing.Status = MembershipStatus.Pending;
                existing.RequestedAt = Now;
                existing.DecidedAt = null;
                break;
            default:
                db.Memberships.Add(new Membership
                {
                    UserId = userId,
                    OrganizationId = organization.Id,
                    Role = role,
                    RequestedAt = Now,
                });
                break;
        }

        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    public Task<List<Membership>> GetForUserAsync(string userId) =>
        db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Include(m => m.Organization)
            .OrderBy(m => m.Organization!.Name).ThenBy(m => m.Role)
            .ToListAsync();

    public Task<List<Membership>> GetForOrganizationAsync(int organizationId) =>
        db.Memberships.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId)
            .Include(m => m.User)
            .OrderBy(m => m.User!.LastName).ThenBy(m => m.User!.FirstName).ThenBy(m => m.Role)
            .ToListAsync();

    public async Task<ServiceResult> DecideAsync(int organizationId, int membershipId, bool approve)
    {
        // Filtering by organisation too means an admin can never decide on another organisation's requests.
        Membership? membership = await db.Memberships
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.OrganizationId == organizationId);
        if (membership is not { Status: MembershipStatus.Pending })
        {
            return ServiceResult.Fail("This request has already been handled.");
        }

        membership.Status = approve ? MembershipStatus.Active : MembershipStatus.Rejected;
        membership.DecidedAt = Now;
        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    public Task<bool> IsActiveAdminAsync(string userId, int organizationId) =>
        db.Memberships.AnyAsync(m =>
            m.UserId == userId &&
            m.OrganizationId == organizationId &&
            m.Role == MembershipRole.Admin &&
            m.Status == MembershipStatus.Active &&
            m.Organization!.Status == OrganizationStatus.Active);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
}
