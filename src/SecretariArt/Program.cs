using SecretariArt.Data;
using SecretariArt.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretariArtData(builder.Configuration);
builder.Services.AddSecretariArtIdentity();
builder.Services.AddSecretariArtServices();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error/500");
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Friendly pages for 403/404 instead of an empty response.
app.UseStatusCodePagesWithReExecute("/error/{0}");

// Before authorization, so the login page gets its CSS.
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

await DbInitializer.InitializeAsync(app.Services);

app.Run();

// Lets the integration tests start the app in memory (WebApplicationFactory<Program>).
public partial class Program { }
