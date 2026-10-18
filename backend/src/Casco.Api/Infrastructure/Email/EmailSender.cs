using System.Net.Mail;
using MailKit.Security;
using MimeKit;
using Casco.Api.Infrastructure;
using System.Net.Mime;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Casco.Api.Infrastructure.Email;

public class SmtpOptions
{
    public string Host { get; set; } = "";
    /// <summary>587 with STARTTLS. This is the sender used for customer mail and dashboard mail.</summary>
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "Casco <no-reply@casco.studio>";
    public bool EnableSsl { get; set; } = true;
    /// <summary>Development: write .eml files here instead of sending (relative to App:DataPath).</summary>
    public string PickupDirectory { get; set; } = "";

    public bool UsesPickup => !string.IsNullOrWhiteSpace(PickupDirectory);
    public bool Enabled => !string.IsNullOrWhiteSpace(From) && (UsesPickup || !string.IsNullOrWhiteSpace(Host));
}

public record EmailMessage(string To, string Subject, string Html, string Text, string Kind);

/// <summary>In-memory outbox. Messages are sent by <see cref="EmailSenderService"/> with retries.</summary>
public class EmailQueue(IOptions<SmtpOptions> options)
{
    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest });
    private int _sent, _failed;

    public ChannelReader<EmailMessage> Reader => _channel.Reader;
    public bool Enabled => options.Value.Enabled;
    public string Mode => !Enabled ? "off" : options.Value.UsesPickup ? "pickup" : "smtp";
    public int Sent => _sent;
    public int Failed => _failed;
    public string? LastError { get; private set; }
    public DateTime? LastSentAt { get; private set; }

    public bool Enqueue(EmailMessage message) => Enabled && !string.IsNullOrWhiteSpace(message.To) && _channel.Writer.TryWrite(message);

    internal void MarkSent()
    {
        Interlocked.Increment(ref _sent);
        LastSentAt = DateTime.UtcNow;
    }

    internal void MarkFailed(string error)
    {
        Interlocked.Increment(ref _failed);
        LastError = error;
    }
}

public static class EmailAssets
{
    public const string LogoContentId = "casco-logo";
    private static readonly Lazy<byte[]> LogoBytes = new(() =>
    {
        using var stream = typeof(EmailAssets).Assembly.GetManifestResourceStream("Casco.Api.Assets.email-logo.png")
                           ?? throw new InvalidOperationException("Embedded email logo is missing");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    });

    public static byte[] Logo => LogoBytes.Value;
    public static string LogoDataUri => "data:image/png;base64," + Convert.ToBase64String(Logo);
}

/// <summary>Shown to the dashboard. <see cref="Permanent"/> failures are not retried.</summary>
public sealed class MailDeliveryException(string message, bool permanent, Exception? inner = null) : Exception(message, inner)
{
    public bool Permanent { get; } = permanent;
}

public class SmtpMailer(IOptions<SmtpOptions> options, IOptions<AppOptions> app)
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var o = options.Value;
        if (o.UsesPickup)
        {
            await SendPickupAsync(message, o, ct);
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new MailKit.Net.Smtp.SmtpClient();
        try
        {
            var secure = o.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : o.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
            await client.ConnectAsync(o.Host, o.Port, secure, budget.Token);
            if (!string.IsNullOrEmpty(o.User))
                await client.AuthenticateAsync(o.User, o.Password, budget.Token);
            using var mime = BuildMime(message, o.From);
            await client.SendAsync(mime, budget.Token);
            await client.DisconnectAsync(true, budget.Token);
        }
        catch (MailDeliveryException)
        {
            throw;
        }
        catch (AuthenticationException ex)
        {
            throw new MailDeliveryException("سيرفر البريد رفض تسجيل الدخول لصندوق الإرسال", permanent: true, ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MailDeliveryException("سيرفر البريد ما ردش في الوقت", permanent: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var permanent = ex.Message.Contains("authenticat", StringComparison.OrdinalIgnoreCase);
            throw new MailDeliveryException("سيرفر البريد رفض الرسالة", permanent, ex);
        }
    }

    private async Task SendPickupAsync(EmailMessage message, SmtpOptions o, CancellationToken ct)
    {
        var dir = Path.IsPathRooted(o.PickupDirectory) ? o.PickupDirectory : Path.Combine(app.Value.DataPath, o.PickupDirectory);
        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);
        using var client = new System.Net.Mail.SmtpClient
        {
            DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
            PickupDirectoryLocation = dir
        };
        using var mail = EmailSenderService.Build(message, o.From);
        await client.SendMailAsync(mail, ct);
    }

    static MimeMessage BuildMime(EmailMessage message, string from)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(from));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        if (message.Kind != "direct")
            mime.Headers.Add("Auto-Submitted", "auto-generated");

        var body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html };
        if (message.Html.Contains("cid:" + EmailAssets.LogoContentId, StringComparison.Ordinal))
        {
            var logo = body.LinkedResources.Add("casco.png", EmailAssets.Logo, MimeKit.ContentType.Parse("image/png"));
            logo.ContentId = EmailAssets.LogoContentId;
        }
        mime.Body = body.ToMessageBody();
        return mime;
    }
}

public class EmailSenderService(EmailQueue queue, SmtpMailer mailer, LeaderElection leader, ILogger<EmailSenderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await leader.WaitUntilLeaderAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }

        await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await mailer.SendAsync(message, stoppingToken);
                    queue.MarkSent();
                    break;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested && attempt < 3 && ex is not MailDeliveryException { Permanent: true })
                {
                    logger.LogWarning("Sending {Kind} email failed (attempt {Attempt}): {Error}", message.Kind, attempt, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(10 * attempt), stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    queue.MarkFailed(ex.Message);
                    logger.LogError(ex, "Could not send {Kind} email to {Domain}", message.Kind, message.To.Split('@').LastOrDefault());
                    break;
                }
            }
        }
    }

    public static MailMessage Build(EmailMessage message, string from)
    {
        var mail = new MailMessage
        {
            From = new MailAddress(from),
            Subject = message.Subject,
            SubjectEncoding = Encoding.UTF8,
            HeadersEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };
        mail.To.Add(message.To);
        if (message.Kind != "direct")
            mail.Headers.Add("Auto-Submitted", "auto-generated");

        var text = AlternateView.CreateAlternateViewFromString(message.Text, Encoding.UTF8, MediaTypeNames.Text.Plain);
        var html = AlternateView.CreateAlternateViewFromString(message.Html, Encoding.UTF8, MediaTypeNames.Text.Html);
        if (message.Html.Contains("cid:" + EmailAssets.LogoContentId, StringComparison.Ordinal))
        {
            var logo = new LinkedResource(new MemoryStream(EmailAssets.Logo), "image/png")
            {
                ContentId = EmailAssets.LogoContentId,
                TransferEncoding = TransferEncoding.Base64
            };
            logo.ContentType.Name = "casco.png";
            html.LinkedResources.Add(logo);
        }
        mail.AlternateViews.Add(text);
        mail.AlternateViews.Add(html);
        return mail;
    }
}
