using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Authorization;
using SecretariArt.Models;

namespace SecretariArt.Data;

public static class DbInitializer
{
    public const string DemoPassword = "Demo!2026";

    public static async Task InitializeAsync(IServiceProvider services)
    {
        // Runs before any request, so there's no request scope: create one for the scoped DbContext and managers.
        using IServiceScope scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecretariArtDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // SQL Server applies the migrations; other providers (SQLite in the tests) build the schema from the model,
        // because the migrations are SQL Server specific.
        if (db.Database.IsSqlServer())
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
        }

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Policies.PlatformAdminRole))
        {
            await roles.CreateAsync(new IdentityRole(Policies.PlatformAdminRole));
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await SeedPlatformAdminAsync(users, configuration.GetSection("PlatformAdmin"));

        if (configuration.GetValue<bool>("SeedDemoData") && !await db.Organizations.AnyAsync())
        {
            await SeedDemoDataAsync(db, users);
        }
    }

    // The platform admin approves new organisations. Credentials come from configuration (user secrets or
    // environment variables in production), never from code.
    private static async Task SeedPlatformAdminAsync(UserManager<ApplicationUser> users, IConfigurationSection settings)
    {
        string? email = settings["Email"];
        string? password = settings["Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        ApplicationUser? admin = await users.FindByEmailAsync(email);
        if (admin == null)
        {
            admin = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FirstName = settings["FirstName"] ?? "Platform",
                LastName = settings["LastName"] ?? "Admin",
            };
            ThrowIfFailed(await users.CreateAsync(admin, password));
        }

        if (!await users.IsInRoleAsync(admin, Policies.PlatformAdminRole))
        {
            ThrowIfFailed(await users.AddToRoleAsync(admin, Policies.PlatformAdminRole));
        }
    }

    // Development only (SeedDemoData): two active organisations, one waiting for approval, and people with
    // every kind of membership, including one person who teaches in one place and studies in another.
    private static async Task SeedDemoDataAsync(SecretariArtDbContext db, UserManager<ApplicationUser> users)
    {
        async Task<ApplicationUser> User(string first, string last, string email, string phone)
        {
            var user = new ApplicationUser
            {
                UserName = email, Email = email, EmailConfirmed = true,
                FirstName = first, LastName = last, PhoneNumber = phone,
            };
            ThrowIfFailed(await users.CreateAsync(user, DemoPassword));
            return user;
        }

        ApplicationUser eleni = await User("Eleni", "Papadopoulou", "eleni@demo.secretariart.app", "+30 210 555 0101");
        ApplicationUser nikos = await User("Nikos", "Georgiou", "nikos@demo.secretariart.app", "+30 261 555 0102");
        ApplicationUser dimitra = await User("Dimitra", "Chatoupi", "dimitra@demo.secretariart.app", "+30 690 555 0103");
        ApplicationUser giorgos = await User("Giorgos", "Alexiou", "giorgos@demo.secretariart.app", "+30 690 555 0104");
        ApplicationUser katerina = await User("Katerina", "Nikolaou", "katerina@demo.secretariart.app", "+30 690 555 0105");
        ApplicationUser sofia = await User("Sofia", "Ioannou", "sofia@demo.secretariart.app", "+30 231 555 0106");

        DateTime now = DateTime.UtcNow;
        var athens = new Organization
        {
            Name = "Athens Drama School", Type = OrganizationType.DramaSchool, City = "Athens",
            ContactEmail = "office@athensdrama.example", Status = OrganizationStatus.Active, InviteCode = "ATHENS26",
            RequestedById = eleni.Id, RequestedAt = now.AddDays(-30), ReviewedAt = now.AddDays(-29),
        };
        var patras = new Organization
        {
            Name = "Patras Dance Studio", Type = OrganizationType.DanceSchool, City = "Patras",
            ContactEmail = "hello@patrasdance.example", Status = OrganizationStatus.Active, InviteCode = "PATRAS26",
            RequestedById = nikos.Id, RequestedAt = now.AddDays(-20), ReviewedAt = now.AddDays(-19),
        };
        var thessaloniki = new Organization
        {
            Name = "Thessaloniki Art Atelier", Type = OrganizationType.Atelier, City = "Thessaloniki",
            ContactEmail = "studio@thessatelier.example", RequestedById = sofia.Id, RequestedAt = now.AddDays(-1),
        };
        db.Organizations.AddRange(athens, patras, thessaloniki);
        await db.SaveChangesAsync();

        Membership Member(ApplicationUser user, Organization organization, MembershipRole role, MembershipStatus status) => new()
        {
            UserId = user.Id, OrganizationId = organization.Id, Role = role, Status = status,
            RequestedAt = now.AddDays(-10), DecidedAt = status == MembershipStatus.Pending ? null : now.AddDays(-9),
        };

        db.Memberships.AddRange(
            Member(eleni, athens, MembershipRole.Admin, MembershipStatus.Active),
            Member(nikos, patras, MembershipRole.Admin, MembershipStatus.Active),
            Member(dimitra, athens, MembershipRole.Teacher, MembershipStatus.Active),
            Member(dimitra, patras, MembershipRole.Student, MembershipStatus.Active),
            Member(giorgos, athens, MembershipRole.Student, MembershipStatus.Active),
            Member(katerina, athens, MembershipRole.Student, MembershipStatus.Pending));
        await db.SaveChangesAsync();
    }

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Seeding failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }
}
