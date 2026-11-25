using System.ComponentModel.DataAnnotations;
using SecretariArt.Models;

namespace SecretariArt.ViewModels;

public class OrganizationRequestViewModel
{
    [Required, StringLength(150, MinimumLength = 2), Display(Name = "Organisation name")]
    public string Name { get; set; } = "";

    [Required, Display(Name = "Type")]
    public OrganizationType? Type { get; set; }

    [Required, StringLength(100)]
    public string City { get; set; } = "";

    [Required, EmailAddress, StringLength(256), Display(Name = "Contact email")]
    public string ContactEmail { get; set; } = "";

    [Display(Name = "Contact phone (optional)")]
    [RegularExpression(@"^\+?[0-9][0-9 ()\-]{6,19}$",
        ErrorMessage = "Enter a phone number with 7 to 20 digits, e.g. +30 210 123 4567.")]
    public string? ContactPhone { get; set; }
}

public class JoinViewModel
{
    [Required, StringLength(20), Display(Name = "Invite code")]
    public string InviteCode { get; set; } = "";

    [Required, Display(Name = "I'm joining as")]
    public MembershipRole? Role { get; set; }

    // Shown when the code came from an invite link, so people see where they're joining.
    public string? OrganizationName { get; set; }
}

public class HomeViewModel
{
    public required string FirstName { get; init; }
    public required IReadOnlyList<Membership> Memberships { get; init; }
    public required IReadOnlyList<Organization> OrganizationRequests { get; init; }

    // Organisation id → join requests waiting for this user's decision (only organisations they administer).
    public required IReadOnlyDictionary<int, int> PendingJoinRequests { get; init; }
}

public class MembersViewModel
{
    public required Organization Organization { get; init; }
    public required IReadOnlyList<Membership> Pending { get; init; }
    public required IReadOnlyList<Membership> Active { get; init; }
    public required string InviteLink { get; init; }
}
