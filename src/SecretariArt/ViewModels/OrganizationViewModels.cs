using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using SecretariArt.Models;

namespace SecretariArt.ViewModels;

public class OrganizationRequestViewModel
{
    [Required(ErrorMessage = "Enter the name of your organisation."), StringLength(150, MinimumLength = 2), Display(Name = "Organisation name")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Choose the kind of organisation."), Display(Name = "Type")]
    public OrganizationType? Type { get; set; }

    [Required(ErrorMessage = "Enter the city."), StringLength(100)]
    public string City { get; set; } = "";

    [Required(ErrorMessage = "Enter an email address we can reach you at."), EmailAddress(ErrorMessage = Messages.InvalidEmail), StringLength(256), Display(Name = "Contact email")]
    public string ContactEmail { get; set; } = "";

    [Display(Name = "Contact phone (optional)")]
    [RegularExpression(@"^\+?[0-9][0-9 ()\-]{6,19}$",
        ErrorMessage = "Enter a phone number with 7 to 20 digits, e.g. +30 210 123 4567.")]
    public string? ContactPhone { get; set; }
}

public class JoinViewModel
{
    [Required(ErrorMessage = "Enter the invite code your organisation gave you."), StringLength(20), Display(Name = "Invite code")]
    public string InviteCode { get; set; } = "";

    [Required(ErrorMessage = "Choose whether you're joining as a student or a teacher."), Display(Name = "I'm joining as")]
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

    // Only for platform admins: organisation requests waiting for a decision.
    public int? PlatformRequestsWaiting { get; init; }
}

public class MembersViewModel
{
    public required Organization Organization { get; init; }
    public required IReadOnlyList<Membership> Pending { get; init; }
    public required IReadOnlyList<Membership> Active { get; init; }

    // The most recent rejections, so an admin can see who was turned away and approve after all.
    public const int RejectedShown = 20;
    public required IReadOnlyList<Membership> Rejected { get; init; }

    public required string InviteLink { get; init; }
}

// The reject page, for a join request or an organisation request: the reason is shown to the person turned down.
public class RejectViewModel
{
    [Required(ErrorMessage = "Give a reason, so they know why."), StringLength(RejectionReasons.MaxLength)]
    [Display(Name = "Reason")]
    public string Reason { get; set; } = "";

    // Filled in by the controller for display; never posted back.
    [ValidateNever] public string Heading { get; set; } = "";
    [ValidateNever] public string Details { get; set; } = "";
    [ValidateNever] public string CancelUrl { get; set; } = "";
}

public class AllOrganizationsViewModel
{
    public required IReadOnlyList<Organization> Organizations { get; init; }

    // The status filter in use; null shows every organisation.
    public OrganizationStatus? Status { get; init; }
}
