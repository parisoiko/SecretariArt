using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SecretariArt.Controllers;

[AllowAnonymous]
public class ErrorController : Controller
{
    [Route("error/{statusCode:int}")]
    public IActionResult Show(int statusCode)
    {
        Response.StatusCode = statusCode;
        return View(statusCode);
    }
}
