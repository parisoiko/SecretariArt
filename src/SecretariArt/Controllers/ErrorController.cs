using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SecretariArt.Controllers;

// Error pages replay the failed request, often a POST (e.g. a sign-in refused by a rate limit): no anti-forgery check
// here, or that POST would end on a 400 instead of the page.
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class ErrorController : Controller
{
    [Route("error/{statusCode:int}")]
    public IActionResult Show(int statusCode)
    {
        Response.StatusCode = statusCode;
        // Re-executing for this page can drop the rate limiter's header: say again how long to wait.
        if (HttpContext.Items[Extensions.RateLimitPolicies.RetryAfterItem] is int seconds)
        {
            Response.Headers.RetryAfter = seconds.ToString();
        }
        return View(statusCode);
    }
}
