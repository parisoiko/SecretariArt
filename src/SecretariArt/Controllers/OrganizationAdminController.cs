using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using SecretariArt.Authorization;
using SecretariArt.Extensions;
using SecretariArt.Models;
using SecretariArt.Services;

namespace SecretariArt.Controllers;

// Base for the administration tabs of one organisation. Every derived route starts with admin/{organizationId}, so
// the OrganizationAdmin policy (which reads that id) protects every action, and the organisation is loaded once for
// the shared header and tabs (_AdminHeader).
[Authorize(Policy = Policies.OrganizationAdmin)]
public abstract class OrganizationAdminController(IOrganizationService organizations) : Controller
{
    protected int OrganizationId => int.Parse(RouteData.Values["organizationId"]!.ToString()!);

    protected Organization Organization => (Organization)ViewData["Organization"]!;

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ViewData["Organization"] = await organizations.FindAsync(OrganizationId);
        await next();
    }

    // After a POST: show the outcome on the next page (Post/Redirect/Get, so a refresh never repeats the action).
    // The organisation id is always added: attribute routes don't reuse it from the current request, and without it
    // the URL falls back to a conventional one that doesn't exist (seen as a 404 in the browser).
    protected IActionResult ReportAndRedirect(ServiceResult result, string action, object? routeValues = null)
    {
        this.Report(result);
        return RedirectToAction(action, OrganizationRoute(routeValues));
    }

    protected RouteValueDictionary OrganizationRoute(object? routeValues = null) =>
        new(routeValues) { ["organizationId"] = OrganizationId };
}
