using System.Globalization;
using System.Text.Json.Nodes;
using Casco.Api.Domain;
using Casco.Api.Features.Billing;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Admin;

public record AccountEntryRequest(string Name, decimal AmountUsd, string Cadence, string OnDate);

/// <summary>Turns saved costs into "what did this period cost?" and a monthly run rate.</summary>
public static class AccountsMath
{
    public static readonly string[] Cadences = ["day", "month", "year", "once"];

    public static decimal Charge(string cadence, decimal amount, DateTime onDate, DateTime from, DateTime toExclusive, DateTime today)
    {
        var start = DateOnly.FromDateTime(onDate);
        var periodFrom = DateOnly.FromDateTime(from);
        var periodTo = DateOnly.FromDateTime(toExclusive).AddDays(-1);
        var end = DateOnly.FromDateTime(today);
        if (periodTo > end) periodTo = end;
        if (periodFrom > periodTo) return 0;
        if (cadence == "once") return start >= periodFrom && start <= periodTo ? amount : 0;
        if (start > periodTo) return 0;
        var activeFrom = start > periodFrom ? start : periodFrom;
        var months = (periodTo.Year - activeFrom.Year) * 12 + periodTo.Month - activeFrom.Month + 1;
        return cadence switch
        {
            "day" => amount * (periodTo.DayNumber - activeFrom.DayNumber + 1),
            "month" => amount * months,
            "year" => Math.Round(amount / 12m * months, 2),
            _ => 0
        };
    }

    /// <summary>Ongoing monthly cost of entries that have already started. One-time payments are not included.</summary>
    public static decimal MonthlyRunRate(IEnumerable<(string Cadence, decimal Amount, DateTime OnDate)> entries, DateTime today)
    {
        var day = DateOnly.FromDateTime(today);
        decimal sum = 0;
        foreach (var e in entries)
        {
            if (DateOnly.FromDateTime(e.OnDate) > day) continue;
            sum += e.Cadence switch
            {
                "day" => e.Amount * 30,
                "month" => e.Amount,
                "year" => Math.Round(e.Amount / 12m, 2),
                _ => 0
            };
        }
        return sum;
    }
}

public class AccountsService(AppDbContext db, IOptions<BillingOptions> billing, TimeProvider clock)
{
    public const string ImportedKey = "accounts.imported";

    public async Task<object> ReportAsync(string? month, string? year, bool all)
    {
        var today = clock.GetUtcNow().UtcDateTime;
        var (from, to, label) = Range(month, year, all, today);
        var entries = await db.AccountEntries.AsNoTracking().OrderByDescending(e => e.OnDate).ThenBy(e => e.Name).ToListAsync();
        var lines = entries.Select(e => new
        {
            e.Id,
            e.Name,
            e.AmountUsd,
            e.Cadence,
            onDate = e.OnDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            inPeriod = AccountsMath.Charge(e.Cadence, e.AmountUsd, e.OnDate, from, to, today)
        }).ToList();

        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatuses.Completed && !p.IsTest && p.CompletedAt >= from && p.CompletedAt < to)
            .Select(p => new { p.Kind, p.AmountMinor }).ToListAsync();
        decimal Income(string? kind) => payments.Where(p => kind is null || p.Kind == kind).Sum(p => p.AmountMinor) / 100m;
        var spent = lines.Sum(l => l.inPeriod);
        var received = Income(null);

        return new
        {
            label,
            from = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = to.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            spent,
            received,
            net = received - spent,
            monthlyRunRate = AccountsMath.MonthlyRunRate(entries.Select(e => (e.Cadence, e.AmountUsd, e.OnDate)), today),
            income = new
            {
                subscriptions = Income(PaymentKinds.Subscription),
                hosting = Income(PaymentKinds.Hosting),
                topups = Income(PaymentKinds.Topup)
            },
            entries = lines
        };
    }

    public async Task<object> SaveAsync(int? id, AccountEntryRequest req)
    {
        var entry = id is null
            ? new AccountEntry { CreatedAt = clock.GetUtcNow().UtcDateTime }
            : await db.AccountEntries.FirstOrDefaultAsync(e => e.Id == id) ?? throw ApiException.NotFound("البند غير موجود");
        Apply(entry, req);
        if (id is null) db.AccountEntries.Add(entry);
        await db.SaveChangesAsync();
        return new { entry.Id };
    }

    public async Task DeleteAsync(int id)
    {
        var entry = await db.AccountEntries.FirstOrDefaultAsync(e => e.Id == id) ?? throw ApiException.NotFound("البند غير موجود");
        db.AccountEntries.Remove(entry);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Moves the old split books (pricing expenses and provider invoices) into account entries once, then clears them
    /// so the dashboard and the prices page no longer keep a second copy.
    /// </summary>
    public async Task ImportLegacyAsync()
    {
        if (await db.AppSettings.AsNoTracking().AnyAsync(s => s.Key == ImportedKey)) return;
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await db.AccountEntries.AnyAsync())
        {
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (var e in PricingSettings.NamedExpenses(billing.Value.Economics))
                db.AccountEntries.Add(new AccountEntry { Name = e.Name, AmountUsd = e.MonthlyUsd, Cadence = "month", OnDate = monthStart, CreatedAt = now });

            var invoices = await db.AiProviderInvoices.ToListAsync();
            foreach (var invoice in invoices)
            {
                if (!DateTime.TryParseExact(invoice.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var on))
                    continue;
                db.AccountEntries.Add(new AccountEntry
                {
                    Name = invoice.Provider,
                    AmountUsd = invoice.AmountUsd,
                    Cadence = "once",
                    OnDate = DateTime.SpecifyKind(on.Date, DateTimeKind.Utc),
                    CreatedAt = now
                });
            }
            db.AiProviderInvoices.RemoveRange(invoices);
        }

        var opt = billing.Value.Economics;
        opt.OperatingExpenses.Clear();
        opt.InfraMonthlyUsd = 0;
        opt.DomainMonthlyUsd = 0;
        opt.EmailMonthlyUsd = 0;

        var saved = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == PricingService.SettingKey);
        if (saved is not null)
        {
            var node = JsonNode.Parse(saved.Value)?.AsObject();
            if (node is not null)
            {
                node["OperatingExpenses"] = new JsonArray();
                node["operatingExpenses"] = new JsonArray();
                node["InfraMonthlyUsd"] = 0;
                node["infraMonthlyUsd"] = 0;
                node["DomainMonthlyUsd"] = 0;
                node["domainMonthlyUsd"] = 0;
                node["EmailMonthlyUsd"] = 0;
                node["emailMonthlyUsd"] = 0;
                saved.Value = node.ToJsonString();
                saved.UpdatedAt = now;
            }
        }

        db.AppSettings.Add(new AppSetting { Key = ImportedKey, Value = "1", UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    public static (DateTime From, DateTime ToExclusive, string Label) Range(string? month, string? year, bool all, DateTime today)
    {
        if (all) return (DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc), today.Date.AddDays(1), "الكل");
        if (!string.IsNullOrWhiteSpace(year) && string.IsNullOrWhiteSpace(month))
        {
            if (!int.TryParse(year, out var y) || y is < 2000 or > 2100) throw ApiException.BadRequest("السنة غير صالحة");
            return (new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(y + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc), year);
        }
        month = string.IsNullOrWhiteSpace(month) ? today.ToString("yyyy-MM", CultureInfo.InvariantCulture) : month;
        var (start, end) = UnitEconomics.MonthRange(month);
        return (start, end, month);
    }

    private static void Apply(AccountEntry entry, AccountEntryRequest req)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length is 0 or > 80) throw ApiException.BadRequest("اسم البند يجب أن يكون بين حرف و80 حرفاً");
        var cadence = (req.Cadence ?? "").Trim().ToLowerInvariant();
        if (!AccountsMath.Cadences.Contains(cadence)) throw ApiException.BadRequest("نوع التكرار غير صالح");
        if (req.AmountUsd is < 0 or > 1_000_000) throw ApiException.BadRequest("المبلغ غير صالح");
        if (!DateTime.TryParseExact(req.OnDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var on))
            throw ApiException.BadRequest("التاريخ يجب أن يكون بالصيغة yyyy-MM-dd");
        entry.Name = name;
        entry.Cadence = cadence;
        entry.AmountUsd = Math.Round(req.AmountUsd, 2);
        entry.OnDate = DateTime.SpecifyKind(on.Date, DateTimeKind.Utc);
    }

    public static void Map(IEndpointRouteBuilder g)
    {
        g.MapGet("/accounts", async (string? month, string? year, bool? all, AccountsService accounts) =>
            Results.Ok(await accounts.ReportAsync(month, year, all == true)));
        g.MapPost("/accounts", async (AccountEntryRequest req, AccountsService accounts) =>
            Results.Ok(await accounts.SaveAsync(null, req)));
        g.MapPut("/accounts/{id:int}", async (int id, AccountEntryRequest req, AccountsService accounts) =>
            Results.Ok(await accounts.SaveAsync(id, req)));
        g.MapDelete("/accounts/{id:int}", async (int id, AccountsService accounts) =>
        {
            await accounts.DeleteAsync(id);
            return Results.NoContent();
        });
    }
}
