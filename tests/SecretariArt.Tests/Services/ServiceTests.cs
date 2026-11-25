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

    public ServiceTests()
    {
        _connection.Open();
        _db = new SecretariArtDbContext(new DbContextOptionsBuilder<SecretariArtDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _organizations = new OrganizationService(_db, new InviteCodeGenerator(), TimeProvider.System);
        _memberships = new MembershipService(_db, TimeProvider.System);
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
    public async Task Joining_IsPendingUntilDecided_AndCanBeAskedAgainAfterRejection()
    {
        Organization organization = await ActiveOrganizationAsync("Corfu Dance", AddUser("corfu"));
        ApplicationUser student = AddUser("student");

        Assert.True((await _memberships.JoinAsync(student.Id, organization.InviteCode!.ToLowerInvariant(), MembershipRole.Student)).Succeeded);
        Assert.False((await _memberships.JoinAsync(student.Id, organization.InviteCode!, MembershipRole.Student)).Succeeded);

        Membership membership = _db.Memberships.Single(m => m.UserId == student.Id);
        await _memberships.DecideAsync(organization.Id, membership.Id, approve: false);
        Assert.Equal(MembershipStatus.Rejected, membership.Status);

        Assert.True((await _memberships.JoinAsync(student.Id, organization.InviteCode!, MembershipRole.Student)).Succeeded);
        Assert.Equal(MembershipStatus.Pending, _db.Memberships.Single(m => m.UserId == student.Id).Status);
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

        ServiceResult result = await _memberships.DecideAsync(mine.Id, requestId, approve: true);

        Assert.False(result.Succeeded);
        Assert.Equal(MembershipStatus.Pending, _db.Memberships.Single(m => m.Id == requestId).Status);
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
