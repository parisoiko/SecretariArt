namespace SecretariArt.Models;

// A school, studio or art space using SecretariArt. Created on request and approved by a platform admin.
public class Organization
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public OrganizationType Type { get; set; }
    public string City { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string? ContactPhone { get; set; }

    public OrganizationStatus Status { get; set; } = OrganizationStatus.Pending;

    // Handed out by the organisation's admins; people use it to ask to join. Only set once approved.
    public string? InviteCode { get; set; }

    public string RequestedById { get; set; } = "";
    public ApplicationUser? RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }

    // Shown to the requester, so a rejected request isn't a dead end.
    public string? RejectionReason { get; set; }

    public List<Membership> Memberships { get; set; } = [];
}

public enum OrganizationType
{
    DramaSchool,
    Conservatoire,
    DanceSchool,
    MusicSchool,
    Atelier,
    RehearsalSpace,
    Other,
}

public enum OrganizationStatus
{
    Pending,
    Active,
    Rejected,
}
