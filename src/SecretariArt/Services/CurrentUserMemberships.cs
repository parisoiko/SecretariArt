using System.Security.Claims;
using SecretariArt.Models;

namespace SecretariArt.Services;

public interface ICurrentUserMemberships
{
    Task<IReadOnlyList<Membership>> GetAsync();
    Task<UserRoles> GetRolesAsync();
}

// What the signed-in user is, across all their organisations. Drives the navigation and the home page.
public record UserRoles(bool IsAdmin, bool IsTeacher, bool IsStudent)
{
    public bool HasAnyRole => IsAdmin || IsTeacher || IsStudent;
}

// Scoped: the layout, the navigation and the page all ask during one request, but the database is queried once.
public class CurrentUserMemberships(IHttpContextAccessor httpContextAccessor, IMembershipService memberships)
    : ICurrentUserMemberships
{
    private IReadOnlyList<Membership>? _cached;

    public async Task<IReadOnlyList<Membership>> GetAsync()
    {
        if (_cached != null)
        {
            return _cached;
        }

        string? userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        _cached = userId == null ? [] : await memberships.GetForUserAsync(userId);
        return _cached;
    }

    public async Task<UserRoles> GetRolesAsync()
    {
        var active = (await GetAsync())
            .Where(m => m.Status == MembershipStatus.Active && m.Organization!.Status == OrganizationStatus.Active)
            .ToList();

        return new UserRoles(
            IsAdmin: active.Any(m => m.Role == MembershipRole.Admin),
            IsTeacher: active.Any(m => m.Role == MembershipRole.Teacher),
            IsStudent: active.Any(m => m.Role == MembershipRole.Student));
    }
}
