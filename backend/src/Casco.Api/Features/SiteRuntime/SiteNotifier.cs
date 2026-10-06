using System.Threading.Channels;
using Casco.Api.Features.Notifications;
using Casco.Api.Infrastructure;
using Casco.Api.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.SiteRuntime;

public record SiteNotification(Guid ProjectId, Guid OwnerId, string? NotifyEmail, SiteEventKind Kind, int? Number, string Summary);

/// <summary>E-mails the site owner about new orders, bookings and messages (only when e-mail is configured).</summary>
public class SiteNotifier(EmailQueue queue)
{
    private readonly Channel<SiteNotification> _queue = Channel.CreateBounded<SiteNotification>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<SiteNotification> Reader => _queue.Reader;

    public void Notify(SiteRef site, SiteEventKind kind, string summary, int? number = null)
    {
        if (!queue.Enabled) return;
        _queue.Writer.TryWrite(new SiteNotification(site.Id, site.OwnerId, site.Settings.NotifyEmail, kind, number, summary));
    }
}

public class SiteNotificationSender(SiteNotifier notifier, EmailQueue emails, IServiceScopeFactory scopes, IOptions<AppOptions> app,
    LeaderElection leader, ILogger<SiteNotificationSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await leader.WaitUntilLeaderAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }

        await foreach (var n in notifier.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var site = await db.Projects.AsNoTracking().Where(p => p.Id == n.ProjectId).Select(p => p.Name).FirstOrDefaultAsync(stoppingToken);
                var owner = await db.Users.AsNoTracking().Where(u => u.Id == n.OwnerId).Select(u => new { u.Email, u.Locale }).FirstOrDefaultAsync(stoppingToken);
                var to = string.IsNullOrWhiteSpace(n.NotifyEmail) ? owner?.Email : n.NotifyEmail;
                if (string.IsNullOrWhiteSpace(to) || site is null) continue;

                var content = EmailCopy.SiteEvent(EmailText.Normalize(owner?.Locale), site, n.Kind, n.Number, n.Summary,
                    $"{app.Value.FrontendBase}/app/p/{n.ProjectId}/data");
                var (html, text) = EmailLayout.Render(content, app.Value.FrontendBase);
                emails.Enqueue(new EmailMessage(to, content.Subject, html, text, "site_" + n.Kind.ToString().ToLowerInvariant()));
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Could not prepare site notification for {ProjectId}", n.ProjectId);
            }
        }
    }
}
