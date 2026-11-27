using Microsoft.AspNetCore.Diagnostics;

namespace SecretariArt.Extensions;

public static class ErrorPageExtensions
{
    // Friendly pages for 403/404/429/500 instead of an empty response: the failed request is run again for
    // /error/{code} (Components/Pages/Error.razor).
    public static WebApplication UseSecretariArtErrorPages(this WebApplication app)
    {
        // A fresh set of request services for the run again: a Blazor page can only be rendered once per scope (its
        // NavigationManager is set up once), and the failed request may have been a Blazor page too.
        app.UseStatusCodePagesWithReExecute("/error/{0}", createScopeForStatusCodePages: true);

        // The request run again keeps its method, often a POST (a sign-in refused by a rate limit). A Blazor page
        // only accepts a POST that names one of its forms, so error pages are always shown as a GET.
        app.Use((context, next) =>
        {
            if (context.Features.Get<IStatusCodeReExecuteFeature>() != null ||
                context.Features.Get<IExceptionHandlerPathFeature>() != null)
            {
                context.Request.Method = HttpMethods.Get;
            }
            return next(context);
        });
        return app;
    }
}
