using Microsoft.AspNetCore.Mvc;
using SecretariArt.Services;

namespace SecretariArt.Extensions;

public static class ControllerExtensions
{
    // Shows the outcome on the next page: a success as "Message", a refusal as "Problem" (see _Messages).
    public static void Report(this Controller controller, ServiceResult result, string? success = null) =>
        controller.TempData[result.Succeeded ? "Message" : "Problem"] =
            result.Succeeded ? result.Message ?? success : result.Error;
}
