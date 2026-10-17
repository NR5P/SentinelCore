namespace SentinelCore.Web;

// Keeps the Organizer's "Admins" group (OrganizerSpaceIds.Admins) the same as the admin accounts,
// so "remind the admins" means whoever runs the house today: promote someone under Accounts and
// within a minute they see the admins' reminders; demote them and those leave their devices on
// the next sync. Polled rather than hooked into every account change - a minute is soon enough,
// and nothing else has to remember to call it.
public sealed class OrganizerAdminsSpaceWorker(IOrganizerStore store, IAccountStore accounts, ILogger<OrganizerAdminsSpaceWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var admins = (await accounts.GetAllAsync(stoppingToken))
                    .Where(account => account.Role == AccountRole.Admin)
                    .Select(account => account.Username)
                    .ToList();
                if (await store.SyncAdminsSpaceAsync(admins, stoppingToken))
                    logger.LogInformation("Organizer Admins space now has {Count} member(s)", admins.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Couldn't update the Organizer Admins space");
            }
            try { await Task.Delay(Every, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
