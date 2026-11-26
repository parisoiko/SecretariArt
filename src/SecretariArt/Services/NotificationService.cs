using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

public interface INotificationService
{
    // Adds the notifications to the unit of work; the caller's SaveChanges stores them with the change they describe.
    void Add(IEnumerable<string> userIds, int organizationId, string text, string? url);
    Task<int> UnreadCountAsync(string userId);
    Task<List<Notification>> GetAsync(string userId, int take = 50);
    Task MarkAllReadAsync(string userId);
}

public class NotificationService(SecretariArtDbContext db, TimeProvider clock) : INotificationService
{
    public void Add(IEnumerable<string> userIds, int organizationId, string text, string? url)
    {
        DateTime now = clock.GetUtcNow().UtcDateTime;
        foreach (string userId in userIds.Distinct())
        {
            db.Notifications.Add(new Notification
            {
                UserId = userId, OrganizationId = organizationId, Text = text, Url = url, CreatedAt = now,
            });
        }
    }

    public Task<int> UnreadCountAsync(string userId) =>
        db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null);

    public Task<List<Notification>> GetAsync(string userId, int take = 50) =>
        db.Notifications.AsNoTracking().Include(n => n.Organization)
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(take).ToListAsync();

    public async Task MarkAllReadAsync(string userId)
    {
        DateTime now = clock.GetUtcNow().UtcDateTime;
        foreach (Notification notification in await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null).ToListAsync())
        {
            notification.ReadAt = now;
        }
        await db.SaveChangesAsync();
    }
}
