using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Models;

namespace SecretariArt.Data;

public class SecretariArtDbContext(DbContextOptions<SecretariArtDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();

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
    }
}
