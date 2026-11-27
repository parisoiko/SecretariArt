using Microsoft.AspNetCore.Mvc;
using SecretariArt.Extensions;
using SecretariArt.Services;

namespace SecretariArt.Controllers;

[Route("notifications")]
public class NotificationsController(INotificationService notifications, ICurrentUserMemberships currentMemberships) : Controller
{
    private string UserId => User.UserId();

    // Shows the latest notifications; unread ones are highlighted on this visit and marked read for the next.
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        // Same rule as the bell (QA, S10): only teachers and students get notifications, so anyone else is sent Home,
        // unless something is still unread.
        UserRoles roles = await currentMemberships.GetRolesAsync();
        if (!roles.IsTeacher && !roles.IsStudent && await notifications.UnreadCountAsync(UserId) == 0)
        {
            return RedirectToAction("Index", "Home");
        }

        var list = await notifications.GetAsync(UserId);
        await notifications.MarkAllReadAsync(UserId);
        return View(list);
    }
}
