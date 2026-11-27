using Microsoft.AspNetCore.Mvc.ViewFeatures;
using SecretariArt.Services;

namespace SecretariArt.Extensions;

// One-off messages that survive a redirect ("Your password has been changed."), shown once by <FlashMessages />
// (Components/Shared/FlashMessages.razor) on the next page. Kept in TempData under the same keys MVC's
// ControllerExtensions.Report uses, so a message set by a Blazor page shows on an MVC page and the other way round,
// while both exist.
public class FlashMessageService(ITempDataDictionaryFactory tempDataFactory)
{
    public const string MessageKey = "Message";  // something happened
    public const string ProblemKey = "Problem";  // nothing happened, and why

    public void Success(HttpContext http, string text) => Set(http, MessageKey, text);

    public void Problem(HttpContext http, string text) => Set(http, ProblemKey, text);

    public void Report(HttpContext http, ServiceResult result, string? success = null)
    {
        if (result.Succeeded)
        {
            Set(http, MessageKey, result.Message ?? success ?? "");
        }
        else
        {
            Set(http, ProblemKey, result.Error ?? "");
        }
    }

    // Reads and removes the messages waiting for this request.
    public (string? Message, string? Problem) Take(HttpContext http)
    {
        ITempDataDictionary tempData = tempDataFactory.GetTempData(http);
        var taken = (tempData[MessageKey] as string, tempData[ProblemKey] as string);
        // MVC saves TempData after every action by itself; outside MVC it must be saved by hand, or what was read
        // would show again on the next page.
        tempData.Save();
        return taken;
    }

    private void Set(HttpContext http, string key, string text)
    {
        ITempDataDictionary tempData = tempDataFactory.GetTempData(http);
        tempData[key] = text;
        tempData.Save();
    }
}
