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
    Task<Membership?> FindPendingAsync(int organizationId, int membershipId);
    Task<ServiceResult> ApproveAsync(int organizationId, int membershipId);
    Task<ServiceResult> RejectAsync(int organizationId, int membershipId, string reason);
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
                TimeSpan wait = (existing.LastRejectedAt ?? existing.DecidedAt ?? DateTime.MinValue) + RejectionReasons.RejoinWait - Now;
                if (wait > TimeSpan.Zero)
                {
                    int minutes = (int)Math.Ceiling(wait.TotalMinutes);
                    return ServiceResult.Fail(
                        $"{organization.Name} turned down your request a moment ago. You can ask again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
                }

                // Asking again re-opens the old request instead of adding a duplicate. LastRejectedAt and the reason
                // stay, so the admin sees this person was turned away before.
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

    public Task<Membership?> FindPendingAsync(int organizationId, int membershipId) =>
        db.Memberships.AsNoTracking()
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.OrganizationId == organizationId &&
                                      m.Status == MembershipStatus.Pending);

    public async Task<ServiceResult> ApproveAsync(int organizationId, int membershipId)
    {
        Membership? membership = await FindForDecisionAsync(organizationId, membershipId);
        if (membership == null)
        {
            return ServiceResult.Fail("We couldn't find that request.");
        }

        // A rejection can be undone by approving after all, so a mistaken Reject isn't final.
        if (membership.Status == MembershipStatus.Active)
        {
            return ServiceResult.Fail("This request has already been handled.");
        }

        membership.Status = MembershipStatus.Active;
        membership.DecidedAt = Now;
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{membership.User!.FullName} is now a {RoleName(membership)} here.");
    }

    public async Task<ServiceResult> RejectAsync(int organizationId, int membershipId, string reason)
    {
        Membership? membership = await FindForDecisionAsync(organizationId, membershipId);
        if (membership == null)
        {
            return ServiceResult.Fail("We couldn't find that request.");
        }

        if (membership.Status != MembershipStatus.Pending)
        {
            return ServiceResult.Fail("This request has already been handled.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return ServiceResult.Fail("Give a reason, so the person knows why.");
        }

        membership.Status = MembershipStatus.Rejected;
        membership.DecidedAt = Now;
        membership.LastRejectedAt = Now;
        membership.RejectionReason = reason.Trim();
        await db.SaveChangesAsync();
        return ServiceResult.Ok(
            $"{membership.User!.FullName}'s request to join as a {RoleName(membership)} was rejected. " +
            "You'll find it under Not approved if you change your mind.");
    }

    public Task<bool> IsActiveAdminAsync(string userId, int organizationId) =>
        db.Memberships.AnyAsync(m =>
            m.UserId == userId &&
            m.OrganizationId == organizationId &&
            m.Role == MembershipRole.Admin &&
            m.Status == MembershipStatus.Active &&
            m.Organization!.Status == OrganizationStatus.Active);

    // Filtering by organisation too means an admin can never decide on another organisation's requests.
    private Task<Membership?> FindForDecisionAsync(int organizationId, int membershipId) =>
        db.Memberships
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.OrganizationId == organizationId);

    private static string RoleName(Membership membership) => membership.Role.ToString().ToLowerInvariant();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
}
