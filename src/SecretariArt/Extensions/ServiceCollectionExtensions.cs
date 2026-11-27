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

        // A password-reset link works for two hours (the default is a day): long enough to find the email, short
        // enough that an old message in someone's inbox is no use to whoever reads it later.
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(2));

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

    public static IServiceCollection AddSecretariArtServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IInviteCodeGenerator, InviteCodeGenerator>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IMembershipService, MembershipService>();
        services.AddScoped<IRulesService, RulesService>();
        services.AddScoped<IClassroomService, ClassroomService>();
        services.AddScoped<ICourseService, CourseService>();
        services.AddScoped<IEnrolmentService, EnrolmentService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<IAvailabilityService, AvailabilityService>();
        services.AddScoped<ITimetableDraftService, TimetableDraftService>();
        services.AddSingleton<GenerationSlots>();
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IPaymentSetupService, PaymentSetupService>();
        services.AddHostedService<BillingWorker>();

        services.Configure<EmailSettings>(configuration.GetSection("Email"));
        services.AddSingleton<IEmailSender, PickupDirectoryEmailSender>();
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        services.AddScoped<FlashMessageService>();

        services.AddHttpContextAccessor();
        // X-Frame-Options comes from SecurityHeaders (DENY), not from anti-forgery (SAMEORIGIN, only on pages with forms).
        services.AddAntiforgery(options => options.SuppressXFrameOptionsHeader = true);
        services.AddScoped<ICurrentUserMemberships, CurrentUserMemberships>();

        // Blazor components, rendered on the server (static SSR) for now.
        services.AddRazorComponents();

        services.AddControllersWithViews(options =>
        {
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());

            // Money and percentages accept a decimal comma ("12,50"), as people in Greece type them.
            options.ModelBinderProviders.Insert(0, new DecimalModelBinderProvider());

            // The framework's wording when a value can't be read at all ("The value '2.5' is not valid for
            // Capacity (people)."). The numeric fields on our forms are whole numbers.
            var messages = options.ModelBindingMessageProvider;
            messages.SetAttemptedValueIsInvalidAccessor((_, field) => $"Enter a whole number for {field}.");
            messages.SetValueMustBeANumberAccessor(field => $"Enter a number for {field}.");
            messages.SetValueIsInvalidAccessor(_ => "Enter a valid value.");
        });

        return services;
    }
}
