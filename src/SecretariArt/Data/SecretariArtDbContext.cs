using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Models;

namespace SecretariArt.Data;

public class SecretariArtDbContext(DbContextOptions<SecretariArtDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<OrganizationRules> OrganizationRules => Set<OrganizationRules>();
    public DbSet<Classroom> Classrooms => Set<Classroom>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseTeacher> CourseTeachers => Set<CourseTeacher>();
    public DbSet<CourseGroup> CourseGroups => Set<CourseGroup>();
    public DbSet<Bundle> Bundles => Set<Bundle>();
    public DbSet<BundleCourse> BundleCourses => Set<BundleCourse>();
    public DbSet<Enrolment> Enrolments => Set<Enrolment>();
    public DbSet<ClassSession> ClassSessions => Set<ClassSession>();
    public DbSet<TeacherAvailability> TeacherAvailability => Set<TeacherAvailability>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ScheduleDraft> ScheduleDrafts => Set<ScheduleDraft>();
    public DbSet<DraftSession> DraftSessions => Set<DraftSession>();
    public DbSet<DraftGap> DraftGaps => Set<DraftGap>();
    public DbSet<PaymentSettings> PaymentSettings => Set<PaymentSettings>();
    public DbSet<Discount> Discounts => Set<Discount>();
    public DbSet<StudentDiscount> StudentDiscounts => Set<StudentDiscount>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Charge> Charges => Set<Charge>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.FirstName).HasMaxLength(100);
            user.Property(u => u.LastName).HasMaxLength(100);
        });

        builder.Entity<Organization>(organization =>
        {
            organization.Property(o => o.Name).HasMaxLength(150);
            organization.Property(o => o.City).HasMaxLength(100);
            organization.Property(o => o.ContactEmail).HasMaxLength(256);
            organization.Property(o => o.ContactPhone).HasMaxLength(30);
            organization.Property(o => o.InviteCode).HasMaxLength(12);
            organization.Property(o => o.RejectionReason).HasMaxLength(RejectionReasons.MaxLength);

            // Enums as text: readable in the database and safe if values are reordered later.
            organization.Property(o => o.Type).HasConversion<string>().HasMaxLength(30);
            organization.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);

            organization.HasIndex(o => o.InviteCode).IsUnique();

            // Deleting a user must not silently delete the organisations they asked for.
            organization.HasOne(o => o.RequestedBy).WithMany()
                .HasForeignKey(o => o.RequestedById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Membership>(membership =>
        {
            membership.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            membership.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            membership.Property(m => m.RejectionReason).HasMaxLength(RejectionReasons.MaxLength);

            // One membership per person, organisation and role (a rejected request can be re-opened, not duplicated).
            membership.HasIndex(m => new { m.UserId, m.OrganizationId, m.Role }).IsUnique();

            membership.HasOne(m => m.User).WithMany(u => u.Memberships)
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            membership.HasOne(m => m.Organization).WithMany(o => o.Memberships)
                .HasForeignKey(m => m.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        ConfigureAdministration(builder);
        ConfigureScheduling(builder);
        ConfigurePayments(builder);
        ConfigureGeneration(builder);
    }

    // Milestone 5. A draft belongs to its organisation and goes with it; its rows go with the draft. Group and room ids
    // are plain values: a draft that refers to something deleted since is refused when used (it's checked again).
    private static void ConfigureGeneration(ModelBuilder builder)
    {
        builder.Entity<ScheduleDraft>(draft =>
        {
            draft.Property(d => d.Mode).HasConversion<string>().HasMaxLength(20);
            draft.HasIndex(d => d.OrganizationId).IsUnique();
            draft.HasOne(d => d.Organization).WithMany().HasForeignKey(d => d.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<DraftSession>(session =>
        {
            session.Property(s => s.Day).HasConversion<string>().HasMaxLength(10);
            session.Property(s => s.GroupLabel).HasMaxLength(250);
            session.HasOne(s => s.Draft).WithMany(d => d.Sessions).HasForeignKey(s => s.DraftId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<DraftGap>(gap =>
        {
            gap.Property(g => g.Reason).HasMaxLength(TextLimits.Long);
            gap.HasOne(g => g.Draft).WithMany(d => d.Gaps).HasForeignKey(g => g.DraftId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    // Milestone 4. Money is decimal(10,2). Charges and payments are never deleted with a student or course: the books
    // must still add up after someone leaves (Restrict, and the services don't delete them).
    private static void ConfigurePayments(ModelBuilder builder)
    {
        builder.Entity<Course>().Property(c => c.Pricing).HasConversion<string>().HasMaxLength(20);
        builder.Entity<Course>().Property(c => c.Price).HasPrecision(10, 2);
        builder.Entity<Bundle>().Property(b => b.MonthlyPrice).HasPrecision(10, 2);

        builder.Entity<PaymentSettings>(settings =>
        {
            settings.HasIndex(s => s.OrganizationId).IsUnique();
            settings.HasOne(s => s.Organization).WithOne().HasForeignKey<PaymentSettings>(s => s.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Discount>(discount =>
        {
            discount.Property(d => d.Name).HasMaxLength(100);
            discount.Property(d => d.Percent).HasPrecision(5, 2);
            discount.HasIndex(d => new { d.OrganizationId, d.Name }).IsUnique();
            discount.HasOne(d => d.Organization).WithMany().HasForeignKey(d => d.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<StudentDiscount>(discount =>
        {
            discount.Property(d => d.Percent).HasPrecision(5, 2);
            discount.Property(d => d.Reason).HasMaxLength(StudentDiscount.ReasonMaxLength);
            discount.HasIndex(d => new { d.OrganizationId, d.StudentId });
            discount.HasOne(d => d.Student).WithMany().HasForeignKey(d => d.StudentId).OnDelete(DeleteBehavior.Restrict);
            discount.HasOne(d => d.Discount).WithMany().HasForeignKey(d => d.DiscountId).OnDelete(DeleteBehavior.Restrict);
            discount.HasOne(d => d.Course).WithMany().HasForeignKey(d => d.CourseId).OnDelete(DeleteBehavior.Restrict);
            discount.HasOne(d => d.Bundle).WithMany().HasForeignKey(d => d.BundleId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Booking>(booking =>
        {
            booking.HasIndex(b => new { b.SessionId, b.Date, b.StudentId }).IsUnique();
            booking.HasIndex(b => new { b.OrganizationId, b.Date });
            // Taking a class off the timetable keeps its past check-ins (still billed) without their class; upcoming ones
            // are cancelled first, and their students told (BookingService.CancelUpcomingAsync).
            booking.HasOne(b => b.Session).WithMany().HasForeignKey(b => b.SessionId).OnDelete(DeleteBehavior.SetNull);
            booking.HasIndex(b => new { b.OrganizationId, b.CourseId });
            booking.HasOne(b => b.Student).WithMany().HasForeignKey(b => b.StudentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Charge>(charge =>
        {
            charge.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
            charge.Property(c => c.Key).HasMaxLength(100);
            charge.Property(c => c.Description).HasMaxLength(200);
            charge.Property(c => c.DiscountNote).HasMaxLength(300);
            charge.Property(c => c.CancelReason).HasMaxLength(RejectionReasons.MaxLength);
            charge.Property(c => c.BaseAmount).HasPrecision(10, 2);
            charge.Property(c => c.DiscountAmount).HasPrecision(10, 2);
            charge.Property(c => c.Amount).HasPrecision(10, 2);
            charge.HasIndex(c => new { c.OrganizationId, c.Key }).IsUnique().HasFilter("[Key] IS NOT NULL");
            charge.HasIndex(c => new { c.OrganizationId, c.DueOn });
            charge.HasIndex(c => c.StudentId);
            charge.HasOne(c => c.Organization).WithMany().HasForeignKey(c => c.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            charge.HasOne(c => c.Student).WithMany().HasForeignKey(c => c.StudentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Payment>(payment =>
        {
            payment.Property(p => p.Amount).HasPrecision(10, 2);
            payment.Property(p => p.Method).HasConversion<string>().HasMaxLength(20);
            payment.Property(p => p.Reference).HasMaxLength(100);
            payment.HasIndex(p => new { p.OrganizationId, p.PaidOn });
            payment.HasOne(p => p.Charge).WithMany(c => c.Payments).HasForeignKey(p => p.ChargeId).OnDelete(DeleteBehavior.Restrict);
            payment.HasOne(p => p.RecordedBy).WithMany().HasForeignKey(p => p.RecordedById).OnDelete(DeleteBehavior.SetNull);
        });
    }

    // Milestone 3. Sessions go with their group (the services delete them explicitly and notify the people involved);
    // a classroom with sessions can't be deleted.
    private static void ConfigureScheduling(ModelBuilder builder)
    {
        builder.Entity<ClassSession>(session =>
        {
            session.Property(s => s.Day).HasConversion<string>().HasMaxLength(10);
            session.HasIndex(s => new { s.OrganizationId, s.Day });
            session.HasOne(s => s.Group).WithMany(g => g.Sessions).HasForeignKey(s => s.GroupId)
                .OnDelete(DeleteBehavior.Restrict);
            session.HasOne(s => s.Classroom).WithMany().HasForeignKey(s => s.ClassroomId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<TeacherAvailability>(availability =>
        {
            availability.Property(a => a.Day).HasConversion<string>().HasMaxLength(10);
            availability.HasIndex(a => new { a.TeacherId, a.OrganizationId });
            availability.HasOne(a => a.Organization).WithMany().HasForeignKey(a => a.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            availability.HasOne(a => a.Teacher).WithMany().HasForeignKey(a => a.TeacherId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Notification>(notification =>
        {
            notification.Property(n => n.Text).HasMaxLength(TextLimits.Long);
            notification.Property(n => n.Url).HasMaxLength(300);
            notification.HasIndex(n => new { n.UserId, n.ReadAt });
            notification.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            notification.HasOne(n => n.Organization).WithMany().HasForeignKey(n => n.OrganizationId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    // Milestone 2. Deletes are Restrict by default: the services remove dependent rows themselves and say so, so
    // nothing disappears silently, and SQL Server never sees two cascade paths into Enrolments.
    private static void ConfigureAdministration(ModelBuilder builder)
    {
        builder.Entity<OrganizationRules>(rules =>
        {
            rules.HasIndex(r => r.OrganizationId).IsUnique();
            rules.HasOne(r => r.Organization).WithOne().HasForeignKey<OrganizationRules>(r => r.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            // Flags as text too ("Monday, Tuesday"); EF parses the comma list back.
            rules.Property(r => r.TeachingDays).HasConversion<string>().HasMaxLength(100);
        });

        builder.Entity<Classroom>(classroom =>
        {
            classroom.Property(c => c.Name).HasMaxLength(100);
            classroom.Property(c => c.Notes).HasMaxLength(500);
            classroom.Property(c => c.Floor).HasConversion<string>().HasMaxLength(30);
            classroom.Property(c => c.Features).HasConversion<string>().HasMaxLength(300);
            classroom.HasIndex(c => new { c.OrganizationId, c.Name }).IsUnique();
            classroom.HasOne(c => c.Organization).WithMany().HasForeignKey(c => c.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Course>(course =>
        {
            course.Property(c => c.Name).HasMaxLength(150);
            course.Property(c => c.Description).HasMaxLength(500);
            course.Property(c => c.RequiredFeatures).HasConversion<string>().HasMaxLength(300);
            course.HasIndex(c => new { c.OrganizationId, c.Name }).IsUnique();
            course.HasOne(c => c.Organization).WithMany().HasForeignKey(c => c.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CourseTeacher>(teacher =>
        {
            teacher.HasKey(t => new { t.CourseId, t.TeacherId });
            teacher.HasOne(t => t.Course).WithMany(c => c.Teachers).HasForeignKey(t => t.CourseId)
                .OnDelete(DeleteBehavior.Cascade);
            teacher.HasOne(t => t.Teacher).WithMany().HasForeignKey(t => t.TeacherId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CourseGroup>(group =>
        {
            group.Property(g => g.Name).HasMaxLength(50);
            group.HasIndex(g => new { g.CourseId, g.Name }).IsUnique();
            group.HasIndex(g => g.OrganizationId);
            group.HasOne(g => g.Course).WithMany(c => c.Groups).HasForeignKey(g => g.CourseId)
                .OnDelete(DeleteBehavior.Restrict);
            group.HasOne(g => g.Teacher).WithMany().HasForeignKey(g => g.TeacherId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Bundle>(bundle =>
        {
            bundle.Property(b => b.Name).HasMaxLength(150);
            bundle.Property(b => b.Description).HasMaxLength(500);
            bundle.HasIndex(b => new { b.OrganizationId, b.Name }).IsUnique();
            bundle.HasOne(b => b.Organization).WithMany().HasForeignKey(b => b.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<BundleCourse>(link =>
        {
            link.HasKey(l => new { l.BundleId, l.CourseId });
            link.HasOne(l => l.Bundle).WithMany(b => b.Courses).HasForeignKey(l => l.BundleId)
                .OnDelete(DeleteBehavior.Cascade);
            link.HasOne(l => l.Course).WithMany(c => c.Bundles).HasForeignKey(l => l.CourseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Enrolment>(enrolment =>
        {
            // A student takes a course in one group only.
            enrolment.HasIndex(e => new { e.StudentId, e.CourseId }).IsUnique();
            enrolment.HasIndex(e => e.OrganizationId);
            enrolment.HasOne(e => e.Student).WithMany().HasForeignKey(e => e.StudentId)
                .OnDelete(DeleteBehavior.Restrict);
            enrolment.HasOne(e => e.Course).WithMany().HasForeignKey(e => e.CourseId)
                .OnDelete(DeleteBehavior.Restrict);
            enrolment.HasOne(e => e.Group).WithMany(g => g.Enrolments).HasForeignKey(e => e.GroupId)
                .OnDelete(DeleteBehavior.Restrict);
            // Deleting a bundle keeps the places it gave; they just no longer say "via Year 1".
            enrolment.HasOne(e => e.Bundle).WithMany().HasForeignKey(e => e.BundleId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
