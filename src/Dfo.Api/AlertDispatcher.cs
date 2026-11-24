using Dfo.Application;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.EntityFrameworkCore;

/// <summary>Placeholder channel: logs instead of sending. Replace with SMTP / WhatsApp provider.</summary>
public class LoggingAlertNotifier(ILogger<LoggingAlertNotifier> log) : IAlertNotifier
{
    public Task SendAsync(AlertMessage m, CancellationToken ct)
    {
        log.LogInformation("ALERT -> {Email} (whatsapp: {Wa}) | {Subject}", m.Email, m.WhatsAppPhone ?? "no", m.Subject);
        return Task.CompletedTask;
    }
}

public class AlertDispatcher(IServiceScopeFactory scopes, ILogger<AlertDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await RunOnce(scope.ServiceProvider.GetRequiredService<DfoDbContext>(),
                              scope.ServiceProvider.GetRequiredService<IAlertNotifier>(), DateTime.UtcNow, ct, log);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Alert dispatch pass failed");   // never let one bad pass kill the loop
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>One pass. Public so it can be driven directly by tests and the "send now" endpoint.</summary>
    public static async Task<int> RunOnce(DfoDbContext db, IAlertNotifier notifier, DateTime now, CancellationToken ct, ILogger? log = null)
    {
        var emailOn = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.EmailNotificationsEnabled ?? true;
        var alerts = await db.SearchAlerts.Where(a => a.IsActive).ToListAsync(ct);
        var sent = 0;

        foreach (var a in alerts.Where(a => AlertMatcher.IsDue(a, now)))
        {
            var since = a.LastCheckedAt ?? a.CreatedAt;
            var fresh = await db.Properties.AsNoTracking()
                .Where(p => p.Status == ListingStatus.Published && p.PublishedAt > since && p.PublishedAt <= now)
                .ToListAsync(ct);
            var hits = fresh.Where(p => AlertMatcher.Matches(a, p)).ToList();

            if (hits.Count > 0 && emailOn)
            {
                var body = string.Join("\n", hits.Take(10).Select(p => $"- {p.Title} ({p.City}) — EGP {p.Price:N0} [{p.ListingCode}]"));
                try
                {
                    await notifier.SendAsync(new AlertMessage(a.Email, a.NotifyWhatsApp ? a.Phone : null,
                        $"{hits.Count} new listing(s) match your search", body), ct);
                    a.LastNotifiedAt = now;
                    a.MatchCount += hits.Count;
                    sent++;
                }
                catch (PermanentEmailException ex)
                {
                    // Dead address: drop these listings (fall through and advance) instead of retrying every minute forever.
                    log?.LogWarning("Dropping alert {AlertId}: {Reason}", a.Id, ex.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Temporary failure: leave LastCheckedAt alone so the same listings are retried next pass,
                    // and keep going so one failing alert cannot block everyone else's.
                    log?.LogWarning("Alert {AlertId} not sent, will retry: {Reason}", a.Id, ex.Message);
                    continue;
                }
            }
            a.LastCheckedAt = now;   // advance even with no hits so the same listings are never re-sent
        }
        await db.SaveChangesAsync(ct);   // alerts already sent are saved even if a later one failed, so nothing is re-sent
        return sent;
    }
}
