using SecretariArt.Data;
using SecretariArt.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretariArtData(builder.Configuration);
builder.Services.AddSecretariArtIdentity();
builder.Services.AddSecretariArtServices(builder.Configuration);
builder.Services.AddSecretariArtRateLimiting(builder.Configuration);
builder.Services.AddSecretariArtHosting(builder.Configuration);

var app = builder.Build();

// First, so everything after sees the visitor's address and scheme, not the proxy's.
app.UseSecretariArtForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error/500");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseSecurityHeaders(app.Environment);

app.UseSecretariArtErrorPages();

// Before authorization, so the login page gets its CSS.
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
// After authentication, so limits can be per signed-in person; after static files, which aren't counted.
app.UseRateLimiter();
app.UseAuthorization();
// Blazor's form posts carry an anti-forgery token; this checks it (the MVC forms use a global filter instead).
app.UseAntiforgery();

app.MapSecretariArtHealthChecks();
app.MapAccountEndpoints();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
// Blazor pages (Components/), next to MVC while the front end moves over: each URL is served by one or the other.
app.MapRazorComponents<SecretariArt.Components.App>();

await DbInitializer.InitializeAsync(app.Services);

app.Run();

// Lets the integration tests start the app in memory (WebApplicationFactory<Program>).
public partial class Program { }
