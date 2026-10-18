using Casco.Api.Domain;
using Casco.Api.Features.Auth;
using Casco.Api.Infrastructure;
using Casco.Api.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Notifications;

/// <summary>Grants the complimentary account and sends the one-time welcome and owner notice.</summary>
public class WelcomeNotifier(AppDbContext db, UserEmails emails, IOptions<AppOptions> app)
{
    public async Task EnsureAsync(User user, CancellationToken ct = default)
    {
        if (!UnlimitedAccount.Matches(user.Email)) return;
        await GrantAsync(user, ct);

        var name = string.IsNullOrWhiteSpace(user.Name) ? "there" : user.Name.Trim();
        var home = app.Value.FrontendBase;
        await emails.SendOnceAsync(
            $"welcome:{UnlimitedAccount.Email}",
            "welcome",
            user.Id,
            user.Email,
            [new EmailContent
            {
                Lang = "en",
                Tone = EmailTone.Success,
                Badge = "Welcome",
                Subject = "Welcome to Casco",
                Preheader = "You are the first person we have sent a welcome email to.",
                Title = "Welcome to Casco",
                Paragraphs =
                [
                    $"Hi {name},",
                    "Thank you for joining Casco. We are glad you are here.",
                    "You are the first person we have sent a welcome email to.",
                    "Your account has unlimited use, on casco.studio and in Casco Studio."
                ],
                Button = new EmailButton("Open Casco", home),
            }],
            ct);

        var owners = app.Value.AdminEmails
            .Append(UnlimitedAccount.OwnerEmail)
            .Select(Text.NormalizeEmail)
            .Where(address => address.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in owners)
        {
            await emails.SendOnceAsync(
                $"welcome-notice:{UnlimitedAccount.Email}:{owner}",
                "welcome-notice",
                user.Id,
                owner,
                [new EmailContent
                {
                    Lang = "ar",
                    Tone = EmailTone.Success,
                    Badge = "حساب جديد",
                    Subject = $"{UnlimitedAccount.Email} سجل في Casco",
                    Preheader = "اتفعل له استخدام من غير حد، واتبعت له إيميل الترحيب.",
                    Title = "حساب جديد",
                    Paragraphs =
                    [
                        $"الحساب {UnlimitedAccount.Email} سجل عندنا.",
                        "اتفعل له استخدام من غير حد، واتبعت له إيميل ترحيب بالإنجليزي."
                    ],
                }],
                ct);
        }
    }

    private async Task GrantAsync(User user, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
        if (sub is null)
        {
            sub = new Subscription { UserId = user.Id };
            db.Subscriptions.Add(sub);
        }
        var now = DateTime.UtcNow;
        if (sub.Plan == PlanKeys.Pro && sub.CurrentPeriodEnd > now.AddYears(50) && sub.NextCreditGrantAt is null)
            return;
        sub.Plan = PlanKeys.Pro;
        sub.Interval = BillingIntervals.Yearly;
        sub.CurrentPeriodStart ??= now;
        sub.CurrentPeriodEnd = now.AddYears(100);
        sub.NextCreditGrantAt = null;
        sub.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }
}
