using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SecretariArt.Data;

namespace SecretariArt.Extensions;

// What running somewhere other than a developer's machine needs: a health check, the real client address behind a
// reverse proxy, and sign-in keys that survive a restart. All driven by configuration (see README, "Deploying").
public static class HostingExtensions
{
    public static IServiceCollection AddSecretariArtHosting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

        // Cookies and reset links are protected with keys that by default live in memory (or the user profile):
        // in a container, a restart would sign everybody out. "DataProtection:KeysDirectory" points at a volume.
        if (configuration["DataProtection:KeysDirectory"] is { Length: > 0 } keys)
        {
            services.AddDataProtection()
                .SetApplicationName("SecretariArt")
                .PersistKeysToFileSystem(new DirectoryInfo(keys));
        }

        // Behind a reverse proxy, the connection comes from the proxy: without this every visitor would share its
        // address (and one rate limit), and the app would think requests arrive over plain HTTP. Only the listed
        // proxies are believed, so nobody else can claim another address with an X-Forwarded-For header.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (string proxy in configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }
            foreach (string network in configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
        return services;
    }

    public static IApplicationBuilder UseSecretariArtForwardedHeaders(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
        {
            app.UseForwardedHeaders();
        }
        return app;
    }

    // For a load balancer or container orchestrator: 200 when the app can reach its database, 503 when it can't.
    // Anonymous and outside the rate limits, since probes come every few seconds.
    public static void MapSecretariArtHealthChecks(this WebApplication app) =>
        app.MapHealthChecks("/health").AllowAnonymous().DisableRateLimiting();
}

public class DatabaseHealthCheck(SecretariArtDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The database can't be reached.");
}
