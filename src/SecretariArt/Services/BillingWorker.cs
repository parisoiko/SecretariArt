using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

// Every hour: makes the charges that have come due (a new month's fees, last month's classes) and sends payment
// reminders, so nothing waits for someone to open the books. Pages run the same billing for what they show, so this
// is only the clock; it can be switched off with Billing:RunInBackground=false (the tests do, to control time).
public class BillingWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<BillingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Billing:RunInBackground", true))
        {
            return;
        }

        // Let start-up (migrations, seeding) finish first.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Billing run failed; trying again in {Minutes} minutes.", Every.TotalMinutes);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SecretariArtDbContext>();
        var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();

        var organizationIds = await db.Organizations.Where(o => o.Status == OrganizationStatus.Active).Select(o => o.Id).ToListAsync(stoppingToken);
        foreach (int organizationId in organizationIds)
        {
            await billing.EnsureChargesAsync(organizationId);
        }
        int reminders = await billing.SendRemindersAsync();
        if (reminders > 0)
        {
            logger.LogInformation("Sent {Count} payment reminders.", reminders);
        }
    }
}
