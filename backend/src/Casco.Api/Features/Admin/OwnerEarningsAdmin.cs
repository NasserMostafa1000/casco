using Casco.Api.Domain;
using Casco.Api.Features.Billing;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Casco.Api.Features.Admin;

public record OwnerLedgerRequest(string Type, decimal Amount, string? Currency, string? Note);
public record AdminNoteRequest(string? Note);

public static class OwnerEarningsAdmin
{
    public static void MapOwnerEarningsAdmin(this RouteGroupBuilder g)
    {
        g.MapGet("/earnings", async (string? search, int? page, AppDbContext db, OwnerEarningsService earnings) =>
        {
            var q = db.Users.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                var lower = s.ToLowerInvariant();
                q = q.Where(u => u.Email.Contains(lower) || u.Name.Contains(s));
            }

            var userIdsWithEarnings = db.OwnerEarningEntries.Select(e => e.UserId).Distinct();
            q = q.Where(u => userIdsWithEarnings.Contains(u.Id) || (u.AdminNote != null && u.AdminNote != ""));

            var current = Math.Max(1, page ?? 1);
            const int pageSize = 50;
            var total = await q.CountAsync();
            var users = await q.OrderByDescending(u => u.CreatedAt).Skip((current - 1) * pageSize).Take(pageSize).ToListAsync();
            var ids = users.Select(u => u.Id).ToList();
            var entries = await db.OwnerEarningEntries.AsNoTracking().Where(e => ids.Contains(e.UserId)).ToListAsync();
            var now = DateTime.UtcNow;
            var projects = await db.Projects.AsNoTracking().Where(p => ids.Contains(p.UserId))
                .Select(p => new { p.UserId, p.HostingTier, p.HostingPaidUntil }).ToListAsync();
            var credits = await db.CreditEntries.AsNoTracking().Where(e => ids.Contains(e.UserId))
                .GroupBy(e => e.UserId).Select(g => new { UserId = g.Key, Sum = g.Sum(e => e.Amount) }).ToListAsync();
            var subs = await db.Subscriptions.AsNoTracking().Where(s => ids.Contains(s.UserId)).ToListAsync();
            var lastPay = await db.Payments.AsNoTracking()
                .Where(p => ids.Contains(p.UserId) && p.Status == PaymentStatuses.Completed)
                .GroupBy(p => p.UserId).Select(g => new { UserId = g.Key, At = g.Max(p => p.CompletedAt) }).ToListAsync();
            var wallets = await db.WalletTransfers.AsNoTracking().Where(t => ids.Contains(t.UserId)).ToListAsync();

            var items = users.Select(u =>
            {
                var rows = entries.Where(e => e.UserId == u.Id).ToList();
                var summary = earnings.BuildSummary(rows);
                var sites = projects.Where(p => p.UserId == u.Id).ToList();
                var sub = subs.FirstOrDefault(s => s.UserId == u.Id);
                var lastWallet = wallets.Where(t => t.UserId == u.Id).OrderByDescending(t => t.CreatedAt).FirstOrDefault();
                return new
                {
                    u.Id, u.Email, u.Name, u.AdminNote, u.CreatedAt,
                    plan = sub != null && sub.Plan == PlanKeys.Pro && sub.CurrentPeriodEnd > now ? PlanKeys.Pro : PlanKeys.Free,
                    credits = credits.FirstOrDefault(c => c.UserId == u.Id)?.Sum ?? 0,
                    sites = sites.Count,
                    backendSites = sites.Count(p => p.HostingTier == HostingTiers.Backend && p.HostingPaidUntil > now),
                    lastPaymentAt = lastPay.FirstOrDefault(p => p.UserId == u.Id)?.At,
                    lastWalletAt = lastWallet?.CreatedAt,
                    lastWalletStatus = lastWallet?.Status,
                    earnings = summary
                };
            }).ToList();

            return Results.Ok(new { total, page = current, pageSize, items });
        });

        g.MapGet("/users/{id:guid}/earnings", async (Guid id, AppDbContext db, OwnerEarningsService earnings) =>
        {
            if (!await db.Users.AnyAsync(u => u.Id == id)) throw ApiException.NotFound("المستخدم غير موجود");
            var rows = await db.OwnerEarningEntries.AsNoTracking().Where(e => e.UserId == id)
                .OrderByDescending(e => e.CreatedAt).Take(100).ToListAsync();
            var note = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.AdminNote).FirstAsync();
            return Results.Ok(new
            {
                adminNote = note,
                summary = earnings.BuildSummary(rows),
                entries = rows.Select(e => new
                {
                    e.Id, e.Type, e.ProjectId, e.OrderId, e.PaymentId,
                    gross = e.GrossMinor / 100m, fee = e.FeeMinor / 100m, net = e.NetMinor / 100m,
                    e.Currency, e.Note, e.CreatedAt
                })
            });
        });

        g.MapPost("/users/{id:guid}/earnings", async (Guid id, OwnerLedgerRequest req, OwnerEarningsService earnings) =>
        {
            var entry = await earnings.RecordAdminAsync(id, req.Type, req.Amount, req.Currency, req.Note);
            return Results.Ok(new
            {
                entry.Id, entry.Type, net = entry.NetMinor / 100m, entry.Currency, entry.Note, entry.CreatedAt
            });
        });

        g.MapPut("/users/{id:guid}/admin-note", async (Guid id, AdminNoteRequest req, AppDbContext db) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id) ?? throw ApiException.NotFound("المستخدم غير موجود");
            user.AdminNote = string.IsNullOrWhiteSpace(req.Note) ? null : Text.Truncate(req.Note.Trim(), 500);
            await db.SaveChangesAsync();
            return Results.Ok(new { adminNote = user.AdminNote });
        });
    }
}
