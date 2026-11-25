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
