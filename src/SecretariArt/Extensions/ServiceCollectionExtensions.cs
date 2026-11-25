using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Authorization;
using SecretariArt.Data;
using SecretariArt.Models;
using SecretariArt.Services;

namespace SecretariArt.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSecretariArtData(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<SecretariArtDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("SecretariArt")));
        return services;
    }

    public static IServiceCollection AddSecretariArtIdentity(this IServiceCollection services)
    {
        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
            })
            .AddEntityFrameworkStores<SecretariArtDbContext>()
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/account/login";
            options.LogoutPath = "/account/logout";
            options.AccessDeniedPath = "/account/access-denied";
        });

        services.AddAuthorization(options =>
        {
            // Everything requires a signed-in user unless marked [AllowAnonymous] (login, register, error pages).
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy(Policies.OrganizationAdmin, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new OrganizationAdminRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, OrganizationAdminHandler>();

        return services;
    }

    public static IServiceCollection AddSecretariArtServices(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IInviteCodeGenerator, InviteCodeGenerator>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IMembershipService, MembershipService>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserMemberships, CurrentUserMemberships>();

        services.AddControllersWithViews(options =>
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

        return services;
    }
}
