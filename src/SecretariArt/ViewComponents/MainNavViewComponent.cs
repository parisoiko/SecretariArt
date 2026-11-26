using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SecretariArt.Authorization;
using SecretariArt.Services;

namespace SecretariArt.ViewComponents;

public record MainNavModel(UserRoles Roles, bool IsPlatformAdmin, string? CurrentController, int UnreadNotifications);

// The sections a user sees depend on their roles across all organisations; the bell shows unread notifications.
public class MainNavViewComponent(ICurrentUserMemberships currentMemberships, INotificationService notifications) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        string? userId = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        return View(new MainNavModel(
            await currentMemberships.GetRolesAsync(),
            User.IsInRole(Policies.PlatformAdminRole),
            RouteData.Values["controller"]?.ToString(),
            userId == null ? 0 : await notifications.UnreadCountAsync(userId)));
    }
}
