using System.Security.Cryptography;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace SecretariArt.Extensions;

// Browser-side defences on every response: what a page may load (Content-Security-Policy), no framing by other
// sites (clickjacking), no MIME sniffing, and no full URLs (an invite link carries its code) in Referer headers.
public static class SecurityHeaders
{
    private const string NonceItem = "CspNonce";

    // Our inline <script> blocks carry this per-request nonce (ScriptNonceTagHelper), so the policy can refuse every
    // other inline script, which is what an injected one would be.
    public static string CspNonce(this HttpContext context) =>
        context.Items[NonceItem] as string ?? "";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, IWebHostEnvironment environment)
    {
        // Hot reload in Development talks to the IDE over a local WebSocket.
        string connect = environment.IsDevelopment() ? "'self' ws://localhost:* wss://localhost:*" : "'self'";

        return app.Use(async (context, next) =>
        {
            // Hex, so HTML encoding leaves it as it is (base64 has "+", which Razor would encode).
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceItem] = nonce;

            // Just before the response starts: the exception handler clears headers set any earlier.
            context.Response.OnStarting(() =>
            {
                IHeaderDictionary headers = context.Response.Headers;
                headers.ContentSecurityPolicy =
                    "default-src 'self'; " +
                    $"script-src 'self' 'nonce-{nonce}' https://cdn.jsdelivr.net; " +
                    // style="" attributes (week grid positions) need 'unsafe-inline'; styles can't run code.
                    "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
                    "font-src 'self' https://cdn.jsdelivr.net https://fonts.gstatic.com; " +
                    "img-src 'self' data:; " +
                    $"connect-src {connect}; " +
                    "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
                headers.XFrameOptions = "DENY";  // for browsers without frame-ancestors
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "same-origin";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                return Task.CompletedTask;
            });
            await next();
        });
    }
}

// Adds the request's CSP nonce to every inline <script> in our views (those without a src).
[HtmlTargetElement("script")]
public class ScriptNonceTagHelper(IHttpContextAccessor http) : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (!output.Attributes.ContainsName("src") && http.HttpContext?.CspNonce() is { Length: > 0 } nonce)
        {
            output.Attributes.SetAttribute("nonce", nonce);
        }
    }
}
