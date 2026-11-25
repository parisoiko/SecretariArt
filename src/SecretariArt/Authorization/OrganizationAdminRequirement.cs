using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SecretariArt.Services;

namespace SecretariArt.Authorization;

public static class Policies
{
    // For routes with an {organizationId}: the user must be an active admin of that organisation.
    public const string OrganizationAdmin = nameof(OrganizationAdmin);

    public const string PlatformAdminRole = "PlatformAdmin";
}

public class OrganizationAdminRequirement : IAuthorizationRequirement;

// Reads {organizationId} from the route, so every admin action is protected by the policy alone:
// a controller can't forget the check, and changing the id in the URL can't reach another organisation.
public class OrganizationAdminHandler(IMembershipService memberships) : AuthorizationHandler<OrganizationAdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, OrganizationAdminRequirement requirement)
    {
        if (context.Resource is not HttpContext http ||
            !int.TryParse(http.GetRouteValue("organizationId")?.ToString(), out int organizationId))
        {
            return;
        }

        string? userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId != null && await memberships.IsActiveAdminAsync(userId, organizationId))
        {
            context.Succeed(requirement);
        }
    }
}
