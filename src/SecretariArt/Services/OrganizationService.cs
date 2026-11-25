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
    Task<ServiceResult> RejectAsync(int organizationId);
    Task<Organization?> FindAsync(int organizationId);
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
        if (await db.Organizations.AnyAsync(o => o.Name == name && o.Status != OrganizationStatus.Rejected))
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

    public async Task<ServiceResult> ApproveAsync(int organizationId)
    {
        Organization? organization = await db.Organizations.FindAsync(organizationId);
        if (organization is not { Status: OrganizationStatus.Pending })
        {
            return ServiceResult.Fail("This request has already been handled.");
        }

        organization.Status = OrganizationStatus.Active;
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

    public async Task<ServiceResult> RejectAsync(int organizationId)
    {
        Organization? organization = await db.Organizations.FindAsync(organizationId);
        if (organization is not { Status: OrganizationStatus.Pending })
        {
            return ServiceResult.Fail("This request has already been handled.");
        }

        organization.Status = OrganizationStatus.Rejected;
        organization.ReviewedAt = Now;
        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

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
