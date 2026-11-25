using Microsoft.AspNetCore.Mvc;
using SecretariArt.Authorization;
using SecretariArt.Services;

namespace SecretariArt.ViewComponents;

public record MainNavModel(UserRoles Roles, bool IsPlatformAdmin, string? CurrentController);

// The sections a user sees depend on their roles across all organisations.
public class MainNavViewComponent(ICurrentUserMemberships currentMemberships) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync() =>
        View(new MainNavModel(
            await currentMemberships.GetRolesAsync(),
            User.IsInRole(Policies.PlatformAdminRole),
            RouteData.Values["controller"]?.ToString()));
}
