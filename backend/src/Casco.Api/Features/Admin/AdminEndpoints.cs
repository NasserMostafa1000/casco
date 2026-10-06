using System.Net.Mail;
using Casco.Api.Domain;
using Casco.Api.Features.Ai;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Projects;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Infrastructure;
using Casco.Api.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Admin;

public record TiersRequest(Dictionary<string, List<string>> Tiers);
/// <param name="PaidAmount">Dollars the user paid for these credits outside Ziina; recorded as revenue when > 0.</param>
public record GrantCreditsRequest(int Amount, string? Note, decimal? PaidAmount = null);
/// <param name="Amount">Dollars received outside Ziina; null/0 activates for free (gift) without recording revenue.</param>
public record ActivatePlanRequest(string Interval, decimal? Amount = null);
public record BroadcastEmailRequest(string? Subject, string? Message);
public record DirectEmailRequest(string? To, string? Subject, string? Message);

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization("admin");
        g.MapSystemMetrics();
        Monitoring.MonitoringEndpoints.MapMonitoring(g);

        g.MapGet("/stats", async (AppDbContext db) =>
        {
            var now = DateTime.UtcNow;
            var since = now.AddDays(-30);
            var usage = db.AiUsages.Where(u => u.CreatedAt >= since);
            return Results.Ok(new
            {
                users = await db.Users.CountAsync(),
                proUsers = await db.Subscriptions.CountAsync(s => s.Plan == PlanKeys.Pro && s.CurrentPeriodEnd > now),
                projects = await db.Projects.CountAsync(),
                publishedSites = await db.Projects.CountAsync(p => p.PublishedAt != null),
                revenue30d = (await db.Payments.Where(p => p.Status == PaymentStatuses.Completed && !p.IsTest && p.CompletedAt >= since)
                    .SumAsync(p => (int?)p.AmountMinor) ?? 0) / 100m,
                aiCost30d = await usage.SumAsync(u => (double?)(u.ActualCostUsd ?? u.EstimatedCostUsd)) ?? 0,
                aiCalls30d = await usage.CountAsync(),
                aiFailures30d = await usage.CountAsync(u => !u.Success),
                responseCacheHits30d = await usage.CountAsync(u => u.ResponseCacheHit),
                inputTokens30d = await usage.SumAsync(u => (long?)u.InputTokens) ?? 0,
                cachedInputTokens30d = await usage.SumAsync(u => (long?)u.CachedInputTokens) ?? 0,
                outputTokens30d = await usage.SumAsync(u => (long?)u.OutputTokens) ?? 0,
                creditsCharged30d = -(await db.CreditEntries.Where(e => e.Type == CreditEntryTypes.Usage && e.CreatedAt >= since).SumAsync(e => (int?)e.Amount) ?? 0),
                tasks30d = await db.AgentTasks.CountAsync(t => t.CreatedAt >= since),
                failedTasks30d = await db.AgentTasks.CountAsync(t => t.CreatedAt >= since && t.Status == TaskStatuses.Failed),
                byModel = await usage.GroupBy(u => u.Model).Select(x => new
                {
                    model = x.Key,
                    calls = x.Count(),
                    cost = x.Sum(u => (double)(u.ActualCostUsd ?? u.EstimatedCostUsd)),
                    inputTokens = x.Sum(u => (long)u.InputTokens),
                    cachedInputTokens = x.Sum(u => (long)u.CachedInputTokens),
                    outputTokens = x.Sum(u => (long)u.OutputTokens)
                }).ToListAsync()
            });
        });

        g.MapGet("/ai", async (ModelCatalog catalog) => Results.Ok(new
        {
            useFake = catalog.Options.UseFake,
            providers = catalog.Options.Providers.Select(p => new { name = p.Key, configured = catalog.IsProviderConfigured(p.Key) }),
            models = catalog.Options.Models.Select(m => new
            {
                m.Id, m.Provider, m.Label, m.InputPer1M, m.CachedInputPer1M, m.OutputPer1M,
                available = catalog.IsProviderConfigured(m.Provider)
            }),
            tiers = await catalog.GetEffectiveTiersAsync(),
            defaults = catalog.Options.Tiers
        }));

        g.MapPut("/ai/tiers", async (TiersRequest req, ModelCatalog catalog) =>
        {
            await catalog.SaveTiersAsync(req.Tiers);
            return Results.Ok(await catalog.GetEffectiveTiersAsync());
        });

        g.MapDelete("/ai/tiers", async (ModelCatalog catalog) =>
        {
            await catalog.ResetTiersAsync();
            return Results.Ok(await catalog.GetEffectiveTiersAsync());
        });

        g.MapAdminUserEndpoints();

        g.MapGet("/pricing", (PricingService pricing) => Results.Ok(new { current = pricing.Current, defaults = pricing.Defaults, updatedAt = pricing.UpdatedAt }));

        g.MapPut("/pricing", async (PricingSettings req, PricingService pricing) =>
            Results.Ok(new { current = await pricing.SaveAsync(req), defaults = pricing.Defaults, updatedAt = pricing.UpdatedAt }));

        g.MapDelete("/pricing", async (PricingService pricing) =>
            Results.Ok(new { current = await pricing.ResetAsync(), defaults = pricing.Defaults, updatedAt = pricing.UpdatedAt }));

        g.MapPost("/users/{id:guid}/credits", async (Guid id, GrantCreditsRequest req, CreditService credits, AppDbContext db, IOptions<BillingOptions> billing) =>
        {
            if (!await db.Users.AnyAsync(u => u.Id == id)) throw ApiException.NotFound();
            if (req.Amount == 0) throw ApiException.BadRequest("عدد النقاط لا يمكن أن يكون صفراً");
            var payment = req.Amount > 0
                ? await AdminUsers.RecordManualPaymentAsync(db, id, req.PaidAmount, billing.Value.Currency, p => { p.Kind = PaymentKinds.Topup; p.TopupCredits = req.Amount; })
                : null;
            await credits.GrantAsync(id, req.Amount, CreditBuckets.Topup, CreditEntryTypes.Admin, payment?.Id.ToString() ?? Text.Truncate(req.Note, 100));
            return Results.NoContent();
        });

        // Manual activation, e.g. for bank transfer or cash payments (Amount > 0 records the payment as revenue).
        g.MapPost("/users/{id:guid}/activate", async (Guid id, ActivatePlanRequest req, SubscriptionService subs, AppDbContext db, IOptions<BillingOptions> billing) =>
        {
            if (!await db.Users.AnyAsync(u => u.Id == id)) throw ApiException.NotFound();
            var interval = req.Interval == BillingIntervals.Yearly ? BillingIntervals.Yearly : BillingIntervals.Monthly;
            var payment = await AdminUsers.RecordManualPaymentAsync(db, id, req.Amount, billing.Value.Currency, p =>
            {
                p.Kind = PaymentKinds.Subscription;
                p.Plan = PlanKeys.Pro;
                p.Interval = interval;
            });
            await subs.ActivateProAsync(id, interval, payment?.Id.ToString() ?? "admin");
            return Results.NoContent();
        });

        g.MapPost("/projects/{id:guid}/hosting", async (Guid id, GrantHostingRequest req, HostingService hosting, AppDbContext db, IOptions<BillingOptions> billing) =>
        {
            var owner = await db.Projects.Where(p => p.Id == id).Select(p => (Guid?)p.UserId).FirstOrDefaultAsync()
                        ?? throw ApiException.NotFound("الموقع غير موجود");
            await hosting.ActivateAsync(id, req.Tier, req.Months);
            await AdminUsers.RecordManualPaymentAsync(db, owner, req.Amount, billing.Value.Currency, p =>
            {
                p.Kind = PaymentKinds.Hosting;
                p.ProjectId = id;
                p.HostingTier = req.Tier;
                p.Interval = req.Months >= 12 ? BillingIntervals.Yearly : BillingIntervals.Monthly;
            });
            return Results.NoContent();
        });

        g.MapPost("/projects/{id:guid}/suspend", async (Guid id, SuspendSiteRequest req, HttpContext http, SiteSuspension suspension) =>
        {
            await suspension.SuspendAsync(id, req.Reason, http.User.UserId());
            return Results.NoContent();
        });

        g.MapPost("/projects/{id:guid}/unsuspend", async (Guid id, HttpContext http, SiteSuspension suspension) =>
        {
            await suspension.UnsuspendAsync(id, http.User.UserId());
            return Results.NoContent();
        });

        g.MapDelete("/projects/{id:guid}", async (Guid id, AppDbContext db, ProjectService projects, UploadService uploads, HostingService hosting, IOptions<AppOptions> appOpt) =>
        {
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id) ?? throw ApiException.NotFound("الموقع غير موجود");
            await projects.DeleteAsync(project, appOpt.Value.DataPath);
            hosting.Invalidate(project);
            await uploads.DeleteProjectAsync(project.Id);
            return Results.NoContent();
        });

        g.MapPost("/email/broadcast", async (BroadcastEmailRequest req, AppDbContext db, EmailQueue queue, IOptions<AppOptions> app, CancellationToken ct) =>
        {
            var (subject, html, text) = AdminMail(queue, app.Value, req.Subject, req.Message);
            var users = await db.Users.AsNoTracking().Where(u => u.Email != "").Select(u => u.Email).ToListAsync(ct);
            var sent = 0;
            foreach (var email in users.Distinct(StringComparer.OrdinalIgnoreCase))
                if (queue.Enqueue(new EmailMessage(email, subject, html, text, "broadcast"))) sent++;
            return Results.Ok(new { sent });
        });

        g.MapPost("/email/send", (DirectEmailRequest req, EmailQueue queue, IOptions<AppOptions> app) =>
        {
            var recipients = ParseRecipients(req.To);
            var (subject, html, text) = AdminMail(queue, app.Value, req.Subject, req.Message);
            var sent = 0;
            foreach (var email in recipients)
                if (queue.Enqueue(new EmailMessage(email, subject, html, text, "direct"))) sent++;
            return Results.Ok(new { sent });
        });

        g.MapGet("/wallet-transfers", async (AppDbContext db) =>
        {
            var items = await db.WalletTransfers.AsNoTracking()
                .OrderByDescending(t => t.CreatedAt).Take(100)
                .Join(db.Users.AsNoTracking(), t => t.UserId, u => u.Id, (t, u) => new
                {
                    t.Id, t.UserId, u.Email, u.Name, t.Method, amount = t.AmountMinor / 100m, t.Currency,
                    t.Status, t.ReviewNote, t.CreatedAt, t.ReviewedAt, hasGoogle = u.GoogleId != null, hasApple = u.AppleId != null
                })
                .ToListAsync();
            return Results.Ok(new { items });
        });

        g.MapGet("/wallet-transfers/{id:guid}/proof", async (Guid id, AppDbContext db, WalletProofStore proofs) =>
        {
            var row = await db.WalletTransfers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id)
                      ?? throw ApiException.NotFound("التحويل غير موجود");
            var stream = proofs.Open(row.ProofKey) ?? throw ApiException.NotFound("صورة التحويل غير موجودة");
            return Results.File(stream, row.ContentType);
        });

        g.MapPost("/wallet-transfers/{id:guid}/approve", async (Guid id, WalletTransferService wallet) =>
        {
            await wallet.ApproveAsync(id);
            return Results.Ok(new { status = WalletTransferStatuses.Approved });
        });

        g.MapPost("/wallet-transfers/{id:guid}/reject", async (Guid id, WalletReview req, WalletTransferService wallet) =>
        {
            await wallet.RejectAsync(id, req.Note);
            return Results.Ok(new { status = WalletTransferStatuses.Rejected });
        });

        g.MapGet("/economics", async (string? month, EconomicsService economics) => Results.Ok(await economics.ReportAsync(month)));
        AccountsService.Map(g);

        g.MapPost("/ziina/webhook", async (ZiinaClient ziina, IOptions<AppOptions> appOpt) =>
        {
            var url = $"{appOpt.Value.PublicUrl.TrimEnd('/')}/api/billing/ziina/webhook";
            await ziina.RegisterWebhookAsync(url);
            return Results.Ok(new { url });
        });
    }

    static (string Subject, string Html, string Text) AdminMail(EmailQueue queue, AppOptions app, string? subjectRaw, string? messageRaw)
    {
        if (!queue.Enabled)
            throw ApiException.BadRequest("الإيميل غير مفعّل: اضبط Smtp في ملف البيئة", "email_disabled");
        var subject = Text.Truncate(subjectRaw?.Trim(), 160);
        var message = Text.Truncate(messageRaw?.Trim(), 4000);
        if (subject.Length < 2 || message.Length < 2) throw ApiException.BadRequest("اكتب عنوان الرسالة ونصها");
        var lang = message.Any(c => c is >= '\u0600' and <= '\u06FF') ? "ar" : "en";
        var paragraphs = message.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var content = new EmailContent
        {
            Lang = lang,
            Subject = subject,
            Title = subject,
            Preheader = paragraphs.FirstOrDefault() ?? subject,
            Paragraphs = paragraphs
        };
        var (html, text) = EmailLayout.Render(content, app.FrontendBase);
        return (subject, html, text);
    }

    static List<string> ParseRecipients(string? raw)
    {
        var parts = (raw ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<string>();
        foreach (var part in parts)
        {
            var email = Text.NormalizeEmail(part);
            if (!MailAddress.TryCreate(email, out var addr) || !addr.Host.Contains('.'))
                throw ApiException.BadRequest("في بريد غير صالح. اكتب كل عنوان لوحده.");
            if (list.Contains(addr.Address, StringComparer.OrdinalIgnoreCase)) continue;
            list.Add(addr.Address);
        }
        if (list.Count == 0) throw ApiException.BadRequest("اكتب بريد واحد على الأقل");
        if (list.Count > 30) throw ApiException.BadRequest("حد أقصى 30 بريد في المرة الواحدة");
        return list;
    }
}
