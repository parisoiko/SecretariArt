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
