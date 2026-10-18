using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Billing;

/// <summary>A partner sees users who signed up this month and 10% of what those users paid. Older accounts are left out.</summary>
public static class PartnerEarnings
{
    public const decimal Share = 0.10m;

    public static void MapPartnerEarnings(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/partner/earnings", async (HttpContext http, AppDbContext db, IOptions<AppOptions> appOpt) =>
        {
            var userId = http.User.UserId();
            var email = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync();
            if (!appOpt.Value.IsPartner(email)) throw ApiException.NotFound("غير موجود");

            var now = DateTime.UtcNow;
            var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var people = await db.Users.AsNoTracking()
                .Where(u => u.CreatedAt >= start && u.Id != userId)
                .OrderBy(u => u.CreatedAt)
                .Select(u => new { u.Id, u.Name })
                .ToListAsync();
            var ids = people.Select(u => u.Id).ToArray();
            var payments = ids.Length == 0
                ? []
                : await db.Payments.AsNoTracking()
                    .Where(p => ids.Contains(p.UserId) && p.Status == PaymentStatuses.Completed && !p.IsTest)
                    .Select(p => new MoneyRow(p.UserId, p.AmountMinor, p.Currency))
                    .ToListAsync();
            var transfers = ids.Length == 0
                ? []
                : await db.WalletTransfers.AsNoTracking()
                    .Where(w => ids.Contains(w.UserId) && w.Status == WalletTransferStatuses.Approved)
                    .Select(w => new MoneyRow(w.UserId, w.AmountMinor, w.Currency))
                    .ToListAsync();
            var paid = payments.Concat(transfers).ToList();

            var users = people.Select(person =>
            {
                var rows = Sum(paid.Where(row => row.UserId == person.Id));
                return new { name = string.IsNullOrWhiteSpace(person.Name) ? "—" : person.Name, paid = rows };
            }).ToList();
            var totals = Sum(paid);
            var profit = totals.Select(row => new { row.Currency, Amount = decimal.Round(row.Amount * Share, 2, MidpointRounding.AwayFromZero) }).ToList();

            return Results.Ok(new
            {
                month = start.ToString("yyyy-MM"),
                count = users.Count,
                users,
                totals,
                profit
            });
        }).RequireAuthorization();
    }

    private static List<MoneyTotal> Sum(IEnumerable<MoneyRow> rows) =>
        rows.GroupBy(row => string.IsNullOrWhiteSpace(row.Currency) ? "USD" : row.Currency.ToUpperInvariant())
            .Select(group => new MoneyTotal(group.Key, group.Sum(row => row.AmountMinor) / 100m))
            .Where(row => row.Amount != 0)
            .OrderBy(row => row.Currency)
            .ToList();

    private sealed record MoneyRow(Guid UserId, int AmountMinor, string Currency);
    private sealed record MoneyTotal(string Currency, decimal Amount);
}
