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

        if (configuration.GetValue<bool>("SeedDemoData"))
        {
            if (!await db.Organizations.AnyAsync())
            {
                await SeedDemoDataAsync(db, users);
            }

            // Milestone 2's demo data is added on its own check, so a database seeded before it gets it too.
            if (!await db.Classrooms.AnyAsync() &&
                await db.Organizations.AnyAsync(o => o.InviteCode == "ATHENS26" || o.InviteCode == "PATRAS26"))
            {
                await SeedDemoAdministrationAsync(db, users);
            }
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

    // Development only: rules, classrooms, courses with groups, a bundle and enrolments for the two demo organisations,
    // plus a second Athens teacher and more Athens students, so every administration tab has something to show.
    private static async Task SeedDemoAdministrationAsync(SecretariArtDbContext db, UserManager<ApplicationUser> users)
    {
        DateTime now = DateTime.UtcNow;
        Organization? athens = await db.Organizations.FirstOrDefaultAsync(o => o.InviteCode == "ATHENS26");
        Organization? patras = await db.Organizations.FirstOrDefaultAsync(o => o.InviteCode == "PATRAS26");

        async Task<ApplicationUser> Person(string first, string last, string email, Organization organization, MembershipRole role)
        {
            ApplicationUser? user = await users.FindByEmailAsync(email);
            if (user == null)
            {
                user = new ApplicationUser
                {
                    UserName = email, Email = email, EmailConfirmed = true, FirstName = first, LastName = last,
                    PhoneNumber = "+30 690 555 0" + Random.Shared.Next(200, 999),
                };
                ThrowIfFailed(await users.CreateAsync(user, DemoPassword));
            }
            if (!await db.Memberships.AnyAsync(m => m.UserId == user.Id && m.OrganizationId == organization.Id && m.Role == role))
            {
                db.Memberships.Add(new Membership
                {
                    UserId = user.Id, OrganizationId = organization.Id, Role = role, Status = MembershipStatus.Active,
                    RequestedAt = now.AddDays(-8), DecidedAt = now.AddDays(-7),
                });
            }
            return user;
        }

        Course AddCourse(Organization organization, string name, int minutes, int perWeek, RoomFeatures needs, params ApplicationUser[] teachers)
        {
            var course = new Course
            {
                OrganizationId = organization.Id, Name = name, SessionMinutes = minutes, SessionsPerWeek = perWeek, RequiredFeatures = needs,
                Teachers = teachers.Select(t => new CourseTeacher { TeacherId = t.Id }).ToList(),
            };
            db.Courses.Add(course);
            return course;
        }

        CourseGroup AddGroup(Course course, string name, int capacity, ApplicationUser? teacher) =>
            new() { OrganizationId = course.OrganizationId, Course = course, Name = name, Capacity = capacity, TeacherId = teacher?.Id };

        void Enrol(ApplicationUser student, CourseGroup group, Bundle? bundle = null) => db.Enrolments.Add(new Enrolment
        {
            OrganizationId = group.OrganizationId, StudentId = student.Id, Course = group.Course, Group = group, Bundle = bundle,
            EnrolledAt = now.AddDays(-5),
        });

        if (athens != null)
        {
            ApplicationUser dimitra = (await users.FindByEmailAsync("dimitra@demo.secretariart.app"))!;
            ApplicationUser petros = await Person("Petros", "Antoniou", "petros@demo.secretariart.app", athens, MembershipRole.Teacher);
            var students = new List<ApplicationUser> { (await users.FindByEmailAsync("giorgos@demo.secretariart.app"))! };
            foreach ((string first, string last) in new[]
                     {
                         ("Maria", "Vlachou"), ("Yannis", "Stathopoulos"), ("Eleftheria", "Kanellou"),
                         ("Christos", "Mavros"), ("Ioanna", "Ralli"), ("Stavros", "Pappas"),
                     })
            {
                students.Add(await Person(first, last, $"{first.ToLowerInvariant()}@demo.secretariart.app", athens, MembershipRole.Student));
            }

            db.OrganizationRules.Add(new OrganizationRules
            {
                OrganizationId = athens.Id, DayStarts = new TimeOnly(10, 0), DayEnds = new TimeOnly(22, 0), SlotMinutes = 15,
                MinSessionMinutes = 45, MaxSessionMinutes = 180, MinSessionsPerWeek = 1, MaxSessionsPerWeek = 3,
                BreakBetweenSessionsMinutes = 15, MaxTeachingHoursPerDay = 8,
                TermStarts = new DateOnly(2026, 9, 14), TermEnds = new DateOnly(2027, 1, 29), UpdatedAt = now.AddDays(-6),
            });
            db.Classrooms.AddRange(
                new Classroom { OrganizationId = athens.Id, Name = "Main stage", Capacity = 40, Floor = FloorType.Stage,
                    Features = RoomFeatures.StageLighting | RoomFeatures.SoundSystem | RoomFeatures.Blackout, StepFreeAccess = true },
                new Classroom { OrganizationId = athens.Id, Name = "Studio A", Capacity = 20, Floor = FloorType.SprungDanceFloor,
                    Features = RoomFeatures.Mirrors | RoomFeatures.SoundSystem, StepFreeAccess = true },
                new Classroom { OrganizationId = athens.Id, Name = "Rehearsal room 1", Capacity = 15, Floor = FloorType.Standard,
                    Features = RoomFeatures.Piano, Notes = "Second floor, no lift" },
                new Classroom { OrganizationId = athens.Id, Name = "Seminar room", Capacity = 25, Floor = FloorType.Carpet,
                    Features = RoomFeatures.Projector, StepFreeAccess = true });

            Course acting = AddCourse(athens, "Acting I", 180, 2, RoomFeatures.None, dimitra, petros);
            Course voice = AddCourse(athens, "Voice and speech", 90, 1, RoomFeatures.Piano, petros);
            Course movement = AddCourse(athens, "Movement for actors", 90, 2, RoomFeatures.Mirrors, dimitra);
            Course history = AddCourse(athens, "Theatre history", 90, 1, RoomFeatures.Projector, petros);
            AddCourse(athens, "Stage combat", 120, 1, RoomFeatures.Mirrors);

            CourseGroup actingA = AddGroup(acting, "A", 12, dimitra), actingB = AddGroup(acting, "B", 12, petros);
            CourseGroup voiceA = AddGroup(voice, "A", 12, petros), voiceB = AddGroup(voice, "B", 12, petros);
            CourseGroup movementA = AddGroup(movement, "A", 16, dimitra);
            CourseGroup historyA = AddGroup(history, "A", 25, petros);
            db.CourseGroups.AddRange(actingA, actingB, voiceA, voiceB, movementA, historyA);

            var year1 = new Bundle
            {
                OrganizationId = athens.Id, Name = "Year 1 – Acting", Description = "The first year of the three-year acting programme.",
                Courses = new[] { acting, voice, movement, history }.Select(c => new BundleCourse { Course = c }).ToList(),
            };
            db.Bundles.Add(year1);

            // Five students through the bundle (four in group A, one in B); two left to enrol by hand.
            for (int i = 0; i < 5; i++)
            {
                bool groupA = i < 4;
                Enrol(students[i], groupA ? actingA : actingB, year1);
                Enrol(students[i], groupA ? voiceA : voiceB, year1);
                Enrol(students[i], movementA, year1);
                Enrol(students[i], historyA, year1);
            }
        }

        if (patras != null)
        {
            ApplicationUser dimitra = (await users.FindByEmailAsync("dimitra@demo.secretariart.app"))!;
            ApplicationUser elena = await Person("Elena", "Rizou", "elena@demo.secretariart.app", patras, MembershipRole.Teacher);

            db.Classrooms.AddRange(
                new Classroom { OrganizationId = patras.Id, Name = "Studio 1", Capacity = 18, Floor = FloorType.SprungDanceFloor,
                    Features = RoomFeatures.Mirrors | RoomFeatures.Barres | RoomFeatures.SoundSystem | RoomFeatures.Piano, StepFreeAccess = true },
                new Classroom { OrganizationId = patras.Id, Name = "Studio 2", Capacity = 12, Floor = FloorType.SprungDanceFloor,
                    Features = RoomFeatures.Mirrors | RoomFeatures.SoundSystem });

            Course ballet = AddCourse(patras, "Ballet for beginners", 60, 2, RoomFeatures.Mirrors | RoomFeatures.Barres, elena);
            Course contemporary = AddCourse(patras, "Contemporary", 90, 1, RoomFeatures.Mirrors, elena);
            CourseGroup balletA = AddGroup(ballet, "A", 14, elena);
            db.CourseGroups.AddRange(balletA, AddGroup(contemporary, "Evening", 12, elena));
            Enrol(dimitra, balletA);
        }

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
