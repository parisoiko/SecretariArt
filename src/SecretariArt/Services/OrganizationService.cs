using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

public record OrganizationRequest(string Name, OrganizationType Type, string City, string ContactEmail, string? ContactPhone);

public interface IOrganizationService
{
    Task<ServiceResult> RequestAsync(string userId, OrganizationRequest request);
    Task<List<Organization>> GetPendingAsync();
    Task<ServiceResult> ApproveAsync(int organizationId);
    Task<ServiceResult> RejectAsync(int organizationId, string reason);
    Task<List<Organization>> GetAllAsync(OrganizationStatus? status);
    Task<Organization?> FindAsync(int organizationId);
    Task<Organization?> FindPendingRequestAsync(int organizationId);
    Task<string> RegenerateInviteCodeAsync(int organizationId);
}

public class OrganizationService(
    SecretariArtDbContext db, IInviteCodeGenerator codes, TimeProvider clock) : IOrganizationService
{
    // Enough for a real person; stops one account from flooding the platform admins with requests.
    public const int MaxPendingRequestsPerUser = 3;

    public async Task<ServiceResult> RequestAsync(string userId, OrganizationRequest request)
    {
        int pending = await db.Organizations
            .CountAsync(o => o.RequestedById == userId && o.Status == OrganizationStatus.Pending);
        if (pending >= MaxPendingRequestsPerUser)
        {
            return ServiceResult.Fail($"You already have {pending} requests waiting for approval.");
        }

        string name = request.Name.Trim();
        var taken = await db.Organizations.Where(o => o.Status != OrganizationStatus.Rejected).Select(o => o.Name).ToListAsync();
        if (taken.Any(n => TextMatch.SameName(n, name)))
        {
            return ServiceResult.Fail("An organisation with this name already exists or is waiting for approval.");
        }

        db.Organizations.Add(new Organization
        {
            Name = name,
            Type = request.Type,
            City = request.City.Trim(),
            ContactEmail = request.ContactEmail.Trim(),
            ContactPhone = string.IsNullOrWhiteSpace(request.ContactPhone) ? null : request.ContactPhone.Trim(),
            RequestedById = userId,
            RequestedAt = Now,
        });
        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    public Task<List<Organization>> GetPendingAsync() =>
        db.Organizations
            .Where(o => o.Status == OrganizationStatus.Pending)
            .Include(o => o.RequestedBy)
            .OrderBy(o => o.RequestedAt)
            .ToListAsync();

    // Every organisation and past decision, for the platform admin's overview. Newest decisions first.
    public Task<List<Organization>> GetAllAsync(OrganizationStatus? status) =>
        db.Organizations.AsNoTracking()
            .Where(o => status == null || o.Status == status)
            .Include(o => o.RequestedBy)
            .OrderByDescending(o => o.ReviewedAt ?? o.RequestedAt)
            .ToListAsync();

    public async Task<ServiceResult> ApproveAsync(int organizationId)
    {
        Organization? organization = await db.Organizations.FindAsync(organizationId);
        if (CannotDecide(organization) is { } refusal)
        {
            return refusal;
        }

        organization!.Status = OrganizationStatus.Active;
        organization.ReviewedAt = Now;
        organization.InviteCode = await NewUniqueCodeAsync();

        // Whoever asked for the organisation becomes its first admin.
        db.Memberships.Add(new Membership
        {
            UserId = organization.RequestedById,
            OrganizationId = organization.Id,
            Role = MembershipRole.Admin,
            Status = MembershipStatus.Active,
            RequestedAt = organization.RequestedAt,
            DecidedAt = Now,
        });

        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> RejectAsync(int organizationId, string reason)
    {
        Organization? organization = await db.Organizations.FindAsync(organizationId);
        if (CannotDecide(organization) is { } refusal)
        {
            return refusal;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return ServiceResult.Fail("Give a reason, so the requester knows why.");
        }

        organization!.Status = OrganizationStatus.Rejected;
        organization.ReviewedAt = Now;
        organization.RejectionReason = reason.Trim();
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"The request for {organization.Name} was rejected.");
    }

    private static ServiceResult? CannotDecide(Organization? organization) => organization switch
    {
        null => ServiceResult.Fail("We couldn't find that request."),
        { Status: not OrganizationStatus.Pending } => ServiceResult.Fail("This request has already been handled."),
        _ => null,
    };

    // A request still waiting for a decision, with who sent it (for the reject page).
    public Task<Organization?> FindPendingRequestAsync(int organizationId) =>
        db.Organizations.AsNoTracking()
            .Include(o => o.RequestedBy)
            .FirstOrDefaultAsync(o => o.Id == organizationId && o.Status == OrganizationStatus.Pending);

    public Task<Organization?> FindAsync(int organizationId) =>
        db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == organizationId);

    // Lets an admin invalidate a code that has been shared too widely.
    public async Task<string> RegenerateInviteCodeAsync(int organizationId)
    {
        Organization organization = await db.Organizations.SingleAsync(o => o.Id == organizationId);
        organization.InviteCode = await NewUniqueCodeAsync();
        await db.SaveChangesAsync();
        return organization.InviteCode;
    }

    private async Task<string> NewUniqueCodeAsync()
    {
        // 31^8 possible codes, so a clash is extremely unlikely; checking keeps the unique index happy anyway.
        string code;
        do
        {
            code = codes.NewCode();
        }
        while (await db.Organizations.AnyAsync(o => o.InviteCode == code));
        return code;
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
}
