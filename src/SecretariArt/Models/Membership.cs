namespace SecretariArt.Models;

// A person's role in one organisation. The same person can be a Teacher in one organisation and a Student in another
// (or both in the same one): every role is its own membership, approved separately.
public class Membership
{
    public int Id { get; set; }

    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public MembershipRole Role { get; set; }
    public MembershipStatus Status { get; set; } = MembershipStatus.Pending;

    public DateTime RequestedAt { get; set; }
    public DateTime? DecidedAt { get; set; }

    // Kept when the person asks again (the same row is re-opened), so the admin sees they were turned away before
    // and why, and so the waiting time before asking again can be enforced.
    public DateTime? LastRejectedAt { get; set; }
    public string? RejectionReason { get; set; }

    // What grants anything: approved, in an organisation that is itself active (not waiting or suspended). For lists
    // already loaded with their Organization; queries use MembershipQueries.Live(). Not stored (no setter).
    public bool IsLive => Status == MembershipStatus.Active && Organization?.Status == OrganizationStatus.Active;
}

public enum MembershipRole
{
    Admin,
    Teacher,
    Student,
}

public enum MembershipStatus
{
    Pending,
    Active,
    Rejected,
}
