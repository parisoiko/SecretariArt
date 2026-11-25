using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;
using SecretariArt.Services;

namespace SecretariArt.Tests.Services;

// The rules of requesting, approving and joining, tested directly against a fresh in-memory database.
public sealed class ServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SecretariArtDbContext _db;
    private readonly OrganizationService _organizations;
    private readonly MembershipService _memberships;
    private readonly MovableClock _clock = new();

    public ServiceTests()
    {
        _connection.Open();
        _db = new SecretariArtDbContext(new DbContextOptionsBuilder<SecretariArtDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _organizations = new OrganizationService(_db, new InviteCodeGenerator(), _clock);
        _memberships = new MembershipService(_db, _clock);
    }

    private ApplicationUser AddUser(string name)
    {
        var user = new ApplicationUser { UserName = $"{name}@test.example", Email = $"{name}@test.example", FirstName = name };
        _db.Users.Add(user);
        _db.SaveChanges();
        return user;
    }

    private static OrganizationRequest Request(string name) =>
        new(name, OrganizationType.DramaSchool, "Athens", "office@test.example", null);

    private async Task<Organization> ActiveOrganizationAsync(string name, ApplicationUser owner)
    {
        await _organizations.RequestAsync(owner.Id, Request(name));
        Organization organization = _db.Organizations.Single(o => o.Name == name);
        await _organizations.ApproveAsync(organization.Id);
        return organization;
    }

    [Fact]
    public async Task Approving_CreatesAnInviteCodeAndMakesTheRequesterAdmin()
    {
        ApplicationUser owner = AddUser("owner");

        Organization organization = await ActiveOrganizationAsync("Volos Drama Lab", owner);

        Assert.Equal(OrganizationStatus.Active, organization.Status);
        Assert.Equal(InviteCodeGenerator.Length, organization.InviteCode!.Length);
        Assert.True(await _memberships.IsActiveAdminAsync(owner.Id, organization.Id));
    }

    [Fact]
    public async Task Approving_Twice_IsRefused()
    {
        Organization organization = await ActiveOrganizationAsync("Twice Studio", AddUser("twice"));

        ServiceResult again = await _organizations.ApproveAsync(organization.Id);

        Assert.False(again.Succeeded);
        Assert.Single(_db.Memberships.Where(m => m.OrganizationId == organization.Id));
    }

    [Fact]
    public async Task Requests_AreLimitedPerUser_AndNamesMustBeUnique()
    {
        ApplicationUser user = AddUser("busy");
        for (int i = 0; i < OrganizationService.MaxPendingRequestsPerUser; i++)
        {
            Assert.True((await _organizations.RequestAsync(user.Id, Request($"School {i}"))).Succeeded);
        }

        Assert.False((await _organizations.RequestAsync(user.Id, Request("One too many"))).Succeeded);
        Assert.False((await _organizations.RequestAsync(AddUser("copycat").Id, Request("School 0"))).Succeeded);
    }

    [Fact]
    public async Task Joining_IsPendingUntilDecided_AndCanBeAskedAgainTenMinutesAfterRejection()
    {
        Organization organization = await ActiveOrganizationAsync("Corfu Dance", AddUser("corfu"));
        ApplicationUser student = AddUser("student");

        Assert.True((await _memberships.JoinAsync(student.Id, organization.InviteCode!.ToLowerInvariant(), MembershipRole.Student)).Succeeded);
        Assert.False((await _memberships.JoinAsync(student.Id, organization.InviteCode!, MembershipRole.Student)).Succeeded);

        Membership membership = _db.Memberships.Single(m => m.UserId == student.Id);
        await _memberships.RejectAsync(organization.Id, membership.Id, "Classes are full.");
        Assert.Equal(MembershipStatus.Rejected, membership.Status);
        Assert.Equal("Classes are full.", membership.RejectionReason);

        _clock.Advance(TimeSpan.FromMinutes(9));
        ServiceResult tooSoon = await _memberships.JoinAsync(student.Id, organization.InviteCode!, MembershipRole.Student);
        Assert.False(tooSoon.Succeeded);
        Assert.Contains("You can ask again in 1 minute.", tooSoon.Error);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await _memberships.JoinAsync(student.Id, organization.InviteCode!, MembershipRole.Student)).Succeeded);

        // Asking again keeps the earlier rejection on record, so the admin sees it.
        Membership reopened = _db.Memberships.Single(m => m.UserId == student.Id);
        Assert.Equal(MembershipStatus.Pending, reopened.Status);
        Assert.NotNull(reopened.LastRejectedAt);
        Assert.Equal("Classes are full.", reopened.RejectionReason);
    }

    [Fact]
    public async Task Rejecting_NeedsAReason()
    {
        Organization organization = await ActiveOrganizationAsync("Reason Studio", AddUser("reasonowner"));
        ApplicationUser student = AddUser("reasonless");
        await _memberships.JoinAsync(student.Id, organization.InviteCode!, MembershipRole.Student);
        int requestId = _db.Memberships.Single(m => m.UserId == student.Id).Id;

        Assert.False((await _memberships.RejectAsync(organization.Id, requestId, "   ")).Succeeded);
        Assert.Equal(MembershipStatus.Pending, _db.Memberships.Single(m => m.Id == requestId).Status);

        await _organizations.RequestAsync(AddUser("hopeful").Id, Request("Hopeful Studio"));
        int pendingOrganization = _db.Organizations.Single(o => o.Name == "Hopeful Studio").Id;
        Assert.False((await _organizations.RejectAsync(pendingOrganization, "")).Succeeded);
        Assert.True((await _organizations.RejectAsync(pendingOrganization, " Not an arts school. ")).Succeeded);
        Assert.Equal("Not an arts school.", _db.Organizations.Single(o => o.Id == pendingOrganization).RejectionReason);
    }

    [Fact]
    public async Task AllOrganisations_CanBeListedByStatus()
    {
        await ActiveOrganizationAsync("Listed Active", AddUser("listedowner"));
        await _organizations.RequestAsync(AddUser("listedwaiting").Id, Request("Listed Waiting"));

        Assert.Equal(2, (await _organizations.GetAllAsync(null)).Count);
        Assert.Equal("Listed Waiting", Assert.Single(await _organizations.GetAllAsync(OrganizationStatus.Pending)).Name);
    }

    [Fact]
    public async Task OnePerson_CanHoldDifferentRolesInDifferentOrganisations()
    {
        Organization drama = await ActiveOrganizationAsync("Drama One", AddUser("dramaowner"));
        Organization dance = await ActiveOrganizationAsync("Dance Two", AddUser("danceowner"));
        ApplicationUser maria = AddUser("maria");

        Assert.True((await _memberships.JoinAsync(maria.Id, drama.InviteCode!, MembershipRole.Teacher)).Succeeded);
        Assert.True((await _memberships.JoinAsync(maria.Id, dance.InviteCode!, MembershipRole.Student)).Succeeded);

        Assert.Equal(2, (await _memberships.GetForUserAsync(maria.Id)).Count);
    }

    [Fact]
    public async Task Joining_AsAdmin_OrWithTheCodeOfAPendingOrganisation_IsRefused()
    {
        Organization organization = await ActiveOrganizationAsync("Admins Only", AddUser("adminowner"));
        ApplicationUser user = AddUser("climber");

        Assert.False((await _memberships.JoinAsync(user.Id, organization.InviteCode!, MembershipRole.Admin)).Succeeded);
        Assert.False((await _memberships.JoinAsync(user.Id, "ZZZZZZZZ", MembershipRole.Student)).Succeeded);
    }

    [Fact]
    public async Task Deciding_OnAnotherOrganisationsRequest_IsRefused()
    {
        Organization mine = await ActiveOrganizationAsync("Mine", AddUser("mineowner"));
        Organization theirs = await ActiveOrganizationAsync("Theirs", AddUser("theirsowner"));
        ApplicationUser student = AddUser("applicant");
        await _memberships.JoinAsync(student.Id, theirs.InviteCode!, MembershipRole.Student);
        int requestId = _db.Memberships.Single(m => m.UserId == student.Id).Id;

        ServiceResult result = await _memberships.ApproveAsync(mine.Id, requestId);

        Assert.False(result.Succeeded);
        Assert.Equal(MembershipStatus.Pending, _db.Memberships.Single(m => m.Id == requestId).Status);
    }

    [Fact]
    public async Task ARejection_CanBeUndoneByApproving_ButNotRejectedTwice()
    {
        Organization organization = await ActiveOrganizationAsync("Undo Studio", AddUser("undoowner"));
        ApplicationUser teacher = AddUser("Kostas");
        await _memberships.JoinAsync(teacher.Id, organization.InviteCode!, MembershipRole.Teacher);
        int requestId = _db.Memberships.Single(m => m.UserId == teacher.Id).Id;

        ServiceResult rejected = await _memberships.RejectAsync(organization.Id, requestId, "Classes are full.");
        Assert.Contains("Kostas", rejected.Message);
        Assert.Contains("teacher", rejected.Message);
        Assert.False((await _memberships.RejectAsync(organization.Id, requestId, "Classes are full.")).Succeeded);

        ServiceResult approved = await _memberships.ApproveAsync(organization.Id, requestId);
        Assert.True(approved.Succeeded);
        Assert.Contains("Kostas", approved.Message);
        Assert.Equal(MembershipStatus.Active, _db.Memberships.Single(m => m.Id == requestId).Status);

        // An active member isn't a request any more: neither decision applies.
        Assert.Equal("This request has already been handled.",
            (await _memberships.RejectAsync(organization.Id, requestId, "Classes are full.")).Error);
    }

    [Fact]
    public async Task DecidingOnARequestThatDoesNotExist_SaysSo()
    {
        Organization organization = await ActiveOrganizationAsync("Missing Studio", AddUser("missingowner"));

        Assert.Equal("We couldn't find that request.",
            (await _memberships.ApproveAsync(organization.Id, 999_999)).Error);
        Assert.Equal("We couldn't find that request.", (await _organizations.ApproveAsync(999_999)).Error);
        Assert.Equal("This request has already been handled.", (await _organizations.ApproveAsync(organization.Id)).Error);
    }

    [Fact]
    public void InviteCodes_AvoidLookAlikeCharacters()
    {
        var generator = new InviteCodeGenerator();
        string codes = string.Concat(Enumerable.Range(0, 500).Select(_ => generator.NewCode()));

        Assert.DoesNotContain(codes, c => "01ILO".Contains(c));
        Assert.Equal("ATHENS26", InviteCodeGenerator.Normalize(" athens-26 "));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}

// A clock the tests can move forward, for rules that depend on time passing (e.g. the wait before asking again).
public sealed class MovableClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
