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

            // Milestone 3: a timetable, availability and a few notifications, once the groups exist.
            if (!await db.ClassSessions.AnyAsync() && await db.CourseGroups.AnyAsync())
            {
                await SeedDemoScheduleAsync(db);
            }

            // Milestone 4: prices, discounts, payment settings and last month's books, once the timetable exists.
            if (!await db.PaymentSettings.AnyAsync() && await db.ClassSessions.AnyAsync())
            {
                await SeedDemoPaymentsAsync(db);
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

    // Development only: Athens' weekly timetable and its teachers' availability, plus Patras' ballet class, which
    // clashes on purpose with a class Dimitra teaches in Athens (her calendar shows it in red). Looked up by name and
    // skipped where something is missing, so it also works on a dev database that has been edited by hand.
    private static async Task SeedDemoScheduleAsync(SecretariArtDbContext db)
    {
        DateTime now = DateTime.UtcNow;
        var groups = await db.CourseGroups.Include(g => g.Course).Include(g => g.Enrolments).ToListAsync();
        var rooms = await db.Classrooms.ToListAsync();
        var organizations = await db.Organizations.Where(o => o.InviteCode == "ATHENS26" || o.InviteCode == "PATRAS26").ToListAsync();

        void Session(string organizationCode, string course, string group, string room, DayOfWeek day, int hour, int minute = 0)
        {
            Organization? organization = organizations.FirstOrDefault(o => o.InviteCode == organizationCode);
            CourseGroup? g = groups.FirstOrDefault(x => x.OrganizationId == organization?.Id && x.Course!.Name == course && x.Name == group);
            Classroom? r = rooms.FirstOrDefault(x => x.OrganizationId == organization?.Id && x.Name == room);
            if (g == null || r == null)
            {
                return;
            }

            var starts = new TimeOnly(hour, minute);
            db.ClassSessions.Add(new ClassSession
            {
                OrganizationId = organization!.Id, GroupId = g.Id, ClassroomId = r.Id, Day = day,
                Starts = starts, Ends = starts.AddMinutes(g.Course!.SessionMinutes), UpdatedAt = now.AddDays(-3),
            });
            var people = g.Enrolments.Select(e => e.StudentId).Concat(g.TeacherId == null ? [] : [g.TeacherId]);
            foreach (string userId in people)
            {
                db.Notifications.Add(new Notification
                {
                    UserId = userId, OrganizationId = organization.Id, CreatedAt = now.AddDays(-3), Url = "/schedule",
                    Text = $"{organization.Name}: New class: {course} (group {group}) on {day} {starts:HH:mm}–{starts.AddMinutes(g.Course.SessionMinutes):HH:mm} in {room}.",
                });
            }
        }

        Session("ATHENS26", "Acting I", "A", "Main stage", DayOfWeek.Monday, 10);
        Session("ATHENS26", "Acting I", "A", "Main stage", DayOfWeek.Thursday, 10);
        Session("ATHENS26", "Acting I", "B", "Main stage", DayOfWeek.Monday, 14);
        Session("ATHENS26", "Acting I", "B", "Main stage", DayOfWeek.Thursday, 14);
        Session("ATHENS26", "Voice and speech", "A", "Rehearsal room 1", DayOfWeek.Tuesday, 10);
        Session("ATHENS26", "Voice and speech", "B", "Rehearsal room 1", DayOfWeek.Tuesday, 12);
        Session("ATHENS26", "Movement for actors", "A", "Studio A", DayOfWeek.Tuesday, 18);
        Session("ATHENS26", "Movement for actors", "A", "Studio A", DayOfWeek.Friday, 10);
        Session("ATHENS26", "Theatre history", "A", "Seminar room", DayOfWeek.Wednesday, 10);
        Session("PATRAS26", "Ballet for beginners", "A", "Studio 1", DayOfWeek.Tuesday, 18);
        Session("PATRAS26", "Ballet for beginners", "A", "Studio 1", DayOfWeek.Thursday, 18);
        Session("PATRAS26", "Contemporary", "Evening", "Studio 2", DayOfWeek.Wednesday, 19);

        Organization? athens = organizations.FirstOrDefault(o => o.InviteCode == "ATHENS26");
        if (athens != null)
        {
            var teachers = await db.Users.Where(u => u.Email == "dimitra@demo.secretariart.app" || u.Email == "petros@demo.secretariart.app")
                .ToDictionaryAsync(u => u.Email!);
            void Available(string email, DayOfWeek day, int from, int to)
            {
                if (teachers.TryGetValue(email, out ApplicationUser? teacher))
                {
                    db.TeacherAvailability.Add(new TeacherAvailability
                    {
                        OrganizationId = athens.Id, TeacherId = teacher.Id, Day = day, From = new TimeOnly(from, 0), To = new TimeOnly(to, 0),
                    });
                }
            }

            Available("dimitra@demo.secretariart.app", DayOfWeek.Monday, 9, 14);
            Available("dimitra@demo.secretariart.app", DayOfWeek.Tuesday, 17, 20);
            Available("dimitra@demo.secretariart.app", DayOfWeek.Thursday, 9, 14);
            Available("dimitra@demo.secretariart.app", DayOfWeek.Friday, 9, 13);
            Available("petros@demo.secretariart.app", DayOfWeek.Monday, 13, 18);
            Available("petros@demo.secretariart.app", DayOfWeek.Tuesday, 9, 14);
            Available("petros@demo.secretariart.app", DayOfWeek.Wednesday, 9, 13);
            Available("petros@demo.secretariart.app", DayOfWeek.Thursday, 13, 18);
        }

        await db.SaveChangesAsync();
    }

    // Development only. Athens: monthly fees and the Year 1 bundle price, two named discounts, and last month's
    // bundle fees (paid, part paid and unpaid, so the books show overdue amounts). Patras: ballet by the month and
    // Contemporary per class, billing no-shows; Dimitra checked in to Contemporary last month (once without coming)
    // and for next Wednesday. This month's fees are made by billing itself, the first time anyone opens the books.
    private static async Task SeedDemoPaymentsAsync(SecretariArtDbContext db)
    {
        DateTime now = DateTime.UtcNow;
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Services.SchoolTime.Zone).DateTime);
        DateOnly lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var organizations = await db.Organizations.Where(o => o.InviteCode == "ATHENS26" || o.InviteCode == "PATRAS26").ToListAsync();
        var courses = await db.Courses.ToListAsync();
        var people = await db.Users.Where(u => u.Email!.EndsWith("@demo.secretariart.app")).ToDictionaryAsync(u => u.Email!.Split('@')[0]);
        var admins = await db.Memberships.Where(m => m.Role == MembershipRole.Admin).ToListAsync();

        void Price(Organization organization, string course, CoursePricing pricing, decimal price)
        {
            if (courses.FirstOrDefault(c => c.OrganizationId == organization.Id && c.Name == course) is Course found)
            {
                found.Pricing = pricing;
                found.Price = price;
            }
        }

        if (organizations.FirstOrDefault(o => o.InviteCode == "ATHENS26") is Organization athens)
        {
            db.PaymentSettings.Add(new PaymentSettings { OrganizationId = athens.Id, DueDay = 5, ReminderDays = 3, UpdatedAt = now });
            Price(athens, "Acting I", CoursePricing.Monthly, 90);
            Price(athens, "Voice and speech", CoursePricing.Monthly, 40);
            Price(athens, "Movement for actors", CoursePricing.Monthly, 50);
            Price(athens, "Theatre history", CoursePricing.Monthly, 35);
            Price(athens, "Stage combat", CoursePricing.Monthly, 45);

            var siblings = new Discount { OrganizationId = athens.Id, Name = "Siblings", Percent = 25 };
            var scholarship = new Discount { OrganizationId = athens.Id, Name = "Scholarship", Percent = 50 };
            db.Discounts.AddRange(siblings, scholarship);

            Bundle? year1 = await db.Bundles.FirstOrDefaultAsync(b => b.OrganizationId == athens.Id && b.Name == "Year 1 – Acting");
            string? eleni = admins.FirstOrDefault(m => m.OrganizationId == athens.Id)?.UserId;
            if (year1 != null)
            {
                year1.MonthlyPrice = 180;
                if (people.TryGetValue("maria", out ApplicationUser? maria))
                {
                    db.StudentDiscounts.Add(new StudentDiscount { OrganizationId = athens.Id, StudentId = maria.Id, Discount = scholarship, CreatedAt = now });
                }
                if (people.TryGetValue("yannis", out ApplicationUser? yannis))
                {
                    db.StudentDiscounts.Add(new StudentDiscount
                    {
                        OrganizationId = athens.Id, StudentId = yannis.Id, Percent = 10, Reason = "Early enrolment", BundleId = year1.Id, CreatedAt = now,
                    });
                }

                // Last month's fees for the five Year 1 students, in every state the books can show.
                (string Name, decimal Amount, string? Note, (decimal Amount, PaymentMethod Method, int Day)? Paid)[] fees =
                [
                    ("giorgos", 180, null, (180, PaymentMethod.Cash, 3)),
                    ("maria", 90, "Scholarship 50%", (90, PaymentMethod.BankTransfer, 4)),
                    ("yannis", 162, "Early enrolment 10%", (100, PaymentMethod.Card, 6)),
                    ("eleftheria", 180, null, null),
                    ("christos", 180, null, (180, PaymentMethod.Card, 5)),
                ];
                foreach (var fee in fees)
                {
                    if (!people.TryGetValue(fee.Name, out ApplicationUser? student))
                    {
                        continue;
                    }
                    var charge = new Charge
                    {
                        OrganizationId = athens.Id, StudentId = student.Id, Kind = ChargeKind.Bundle, Key = $"B:{year1.Id}:{student.Id}:{lastMonth:yyyy-MM}",
                        BundleId = year1.Id, Month = lastMonth, Description = $"{year1.Name} (bundle), {DisplayNames.MonthName(lastMonth)}",
                        BaseAmount = 180, DiscountAmount = 180 - fee.Amount, DiscountNote = fee.Note, Amount = fee.Amount,
                        DueOn = lastMonth.AddDays(4), CreatedAt = now.AddDays(-30), ReminderSentAt = now.AddDays(-33),
                    };
                    if (fee.Paid is var (amount, method, day))
                    {
                        charge.Payments.Add(new Payment
                        {
                            OrganizationId = athens.Id, Amount = amount, Method = method, PaidOn = lastMonth.AddDays(day - 1),
                            RecordedById = eleni, RecordedAt = now.AddDays(-30),
                        });
                    }
                    db.Charges.Add(charge);
                }
            }

            if (people.TryGetValue("ioanna", out ApplicationUser? ioanna))
            {
                db.Charges.Add(new Charge
                {
                    OrganizationId = athens.Id, StudentId = ioanna.Id, Kind = ChargeKind.Other, Description = "Registration fee 2026–27",
                    BaseAmount = 50, Amount = 50, DueOn = today.AddDays(2), CreatedAt = now.AddDays(-10),
                });
            }
        }

        if (organizations.FirstOrDefault(o => o.InviteCode == "PATRAS26") is Organization patras)
        {
            db.PaymentSettings.Add(new PaymentSettings
            {
                OrganizationId = patras.Id, DueDay = 1, ReminderDays = 2, BillNoShows = true, CancelHours = 4, UpdatedAt = now,
            });
            Price(patras, "Ballet for beginners", CoursePricing.Monthly, 60);
            Price(patras, "Contemporary", CoursePricing.PerClass, 15);

            ClassSession? contemporary = await db.ClassSessions.Include(s => s.Group)
                .FirstOrDefaultAsync(s => s.OrganizationId == patras.Id && s.Group!.Course!.Name == "Contemporary");
            if (contemporary != null && people.TryGetValue("dimitra", out ApplicationUser? dimitra))
            {
                if (!await db.Enrolments.AnyAsync(e => e.StudentId == dimitra.Id && e.CourseId == contemporary.Group!.CourseId))
                {
                    db.Enrolments.Add(new Enrolment
                    {
                        OrganizationId = patras.Id, StudentId = dimitra.Id, CourseId = contemporary.Group!.CourseId, GroupId = contemporary.GroupId,
                        EnrolledAt = now.AddDays(-40),
                    });
                }

                DateOnly NextDay(DateOnly from, DayOfWeek day) => from.AddDays(((int)day - (int)from.DayOfWeek + 7) % 7);
                DateOnly first = NextDay(lastMonth, contemporary.Day);
                DateOnly upcoming = NextDay(today.AddDays(1), contemporary.Day);
                foreach ((DateOnly date, bool noShow) in new[] { (first, false), (first.AddDays(7), true), (upcoming, false) })
                {
                    db.Bookings.Add(new Booking
                    {
                        OrganizationId = patras.Id, SessionId = contemporary.Id, CourseId = contemporary.Group!.CourseId, Date = date, StudentId = dimitra.Id,
                        BookedAt = now.AddDays(-35), NoShow = noShow,
                    });
                }
            }
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
