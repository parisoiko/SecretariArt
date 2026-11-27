using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using SecretariArt.Models;
using SecretariArt.Services;

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
    [RegularExpression(Messages.PhonePattern,
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

    // "Needs your attention": the join requests themselves, newest first, in the organisations this person runs.
    public IReadOnlyList<Membership> JoinRequests { get; init; } = [];

    // "Coming up": today's classes (taught or attended) and payments due soon or overdue.
    public DateOnly Today { get; init; }
    public IReadOnlyList<PersonalSession> TodaysClasses { get; init; } = [];
    public IReadOnlyList<Charge> PaymentsDue { get; init; } = [];

    // Only for platform admins: organisation requests waiting for a decision.
    public int? PlatformRequestsWaiting { get; init; }

    // One card per organisation where this person is live, with their roles there. Grouped by id: each membership
    // carries its own copy of the organisation, so grouping by the object would give one card per role.
    public IReadOnlyList<IGrouping<int, Membership>> ActiveOrganizations =>
        Memberships.Where(m => m.IsLive).GroupBy(m => m.OrganizationId).ToList();

    // Join requests this person sent that are still pending, or were turned down.
    public IReadOnlyList<Membership> WaitingMemberships => Memberships.Where(m => m.Status != MembershipStatus.Active).ToList();

    public bool IsPlatformAdmin => PlatformRequestsWaiting != null;

    public bool HasRequests => WaitingMemberships.Count > 0 || OrganizationRequests.Count > 0;

    // Someone with nothing yet gets the welcome; a platform admin has their own work.
    public bool IsNewcomer => ActiveOrganizations.Count == 0 && !IsPlatformAdmin;
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

    public required SetupProgress Setup { get; init; }

    // One row per person, with a badge for each role they hold here: admins first, then by name.
    public IReadOnlyList<IGrouping<string, Membership>> People => Active
        .GroupBy(m => m.UserId)
        .OrderBy(g => g.Min(m => m.Role)).ThenBy(g => g.First().User!.LastName).ThenBy(g => g.First().User!.FirstName)
        .ToList();

    public int CountWith(MembershipRole role) => Active.Where(m => m.Role == role).Select(m => m.UserId).Distinct().Count();
}

// The "Getting started" checklist on the Members page, hidden once everything is done.
public record SetupProgress(bool RulesSaved, bool HasClassrooms, bool HasCourseWithGroup, bool HasStudents, bool HasEnrolments)
{
    public bool IsComplete => RulesSaved && HasClassrooms && HasCourseWithGroup && HasStudents && HasEnrolments;

    // The checklist in order, each step with the admin page that does it.
    public IReadOnlyList<SetupStep> Steps =>
    [
        new(RulesSaved, "Set your timetabling rules", "Rules", "Edit"),
        new(HasClassrooms, "Add your classrooms", "Classrooms", "Index"),
        new(HasCourseWithGroup, "Add a course with a group", "Courses", "Index"),
        new(HasStudents, "Invite and approve students (below)", "Administration", "Members"),
        new(HasEnrolments, "Enrol students", "Enrolments", "Index"),
    ];
}

public record SetupStep(bool Done, string Label, string Controller, string Action);

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
