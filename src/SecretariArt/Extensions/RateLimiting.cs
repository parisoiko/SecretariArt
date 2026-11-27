using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace SecretariArt.Extensions;

// How many requests each kind of action allows, per window. Read from the "RateLimits" section, so a deployment (or
// the tests) can change them without code. Each limit is per signed-in person, or per IP address for visitors.
public class RateLimitSettings
{
    // Every request, as a flood guard: far above what a person clicking through the site does.
    public RateLimit Global { get; set; } = new(600, 60);

    // Password guessing. Identity also locks an account after 5 wrong passwords; this limits trying many accounts
    // from one address. Generous enough for a school whose students share one address.
    public RateLimit SignIn { get; set; } = new(30, 60);

    // Fake accounts. A class registering together from one school network still fits.
    public RateLimit Register { get; set; } = new(20, 600);

    // Guessing invite codes to discover organisations (the join form and /join/{code} links).
    public RateLimit InviteCodes { get; set; } = new(10, 600);

    // Spam organisation requests to the platform admins.
    public RateLimit OrganizationRequests { get; set; } = new(5, 3600);

    // Generating a timetable takes seconds of CPU: enough tries for real work, not for keeping the server busy.
    public RateLimit Generation { get; set; } = new(10, 600);
}

// A plain class (not a positional record), so configuration can override one value and keep the other.
public class RateLimit
{
    public RateLimit() { }

    public RateLimit(int permitLimit, int windowSeconds) => (PermitLimit, WindowSeconds) = (permitLimit, windowSeconds);

    public int PermitLimit { get; set; }
    public int WindowSeconds { get; set; }
}

public static class RateLimitPolicies
{
    public const string SignIn = "sign-in";
    public const string Register = "register";
    public const string InviteCodes = "invite-codes";
    public const string OrganizationRequests = "organization-requests";
    public const string Generation = "generation";

    // Where the "too many requests" page reads how long to wait.
    public const string RetryAfterItem = "RetryAfterSeconds";
    private const string SegmentItem = "RateLimitSegmentSeconds";

    public static IServiceCollection AddSecretariArtRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitSettings>(configuration.GetSection("RateLimits"));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Everything except the error pages: the "too many requests" page itself must always be reachable.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/error")
                    ? RateLimitPartition.GetNoLimiter("error-pages")
                    : Window(context, s => s.Global, "global"));

            options.AddPolicy(SignIn, context => Window(context, s => s.SignIn, SignIn, byAddress: true));
            options.AddPolicy(Register, context => Window(context, s => s.Register, Register, byAddress: true));
            options.AddPolicy(InviteCodes, context => Window(context, s => s.InviteCodes, InviteCodes));
            options.AddPolicy(OrganizationRequests, context => Window(context, s => s.OrganizationRequests, OrganizationRequests));
            options.AddPolicy(Generation, context => Window(context, s => s.Generation, Generation));

            options.OnRejected = (context, _) =>
            {
                HttpContext http = context.HttpContext;
                int? seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter)
                    ? (int)Math.Ceiling(retryAfter.TotalSeconds)
                    : http.Items[SegmentItem] as int?;
                if (seconds > 0)
                {
                    http.Response.Headers.RetryAfter = seconds.ToString();
                    http.Items[RetryAfterItem] = seconds;
                }
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SecretariArt.RateLimiting")
                    .LogWarning("Rate limit hit on {Path} by {Partition}.", http.Request.Path, PartitionKey(http, byAddress: false));
                return ValueTask.CompletedTask;
            };
        });
        return services;
    }

    // A sliding window (four segments), so a burst at the edge of one window can't double the limit. Sign-in and
    // registration are counted per address even when someone is signed in: they are what an attacker scripts.
    private static RateLimitPartition<string> Window(HttpContext context, Func<RateLimitSettings, RateLimit> pick, string policy, bool byAddress = false)
    {
        RateLimit limit = pick(context.RequestServices.GetRequiredService<IOptionsMonitor<RateLimitSettings>>().CurrentValue);
        // A sliding window frees permits one segment at a time: the longest wait if this one refuses.
        context.Items[SegmentItem] = (int)Math.Ceiling(limit.WindowSeconds / 4.0);
        return RateLimitPartition.GetSlidingWindowLimiter($"{policy}:{PartitionKey(context, byAddress)}", _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = limit.PermitLimit,
            Window = TimeSpan.FromSeconds(limit.WindowSeconds),
            SegmentsPerWindow = 4,
            QueueLimit = 0,
        });
    }

    // Behind a reverse proxy, RemoteIpAddress is the proxy's: configure forwarded headers there (see CLAUDE.md).
    private static string PartitionKey(HttpContext context, bool byAddress) =>
        !byAddress && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is string userId
            ? $"user:{userId}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
