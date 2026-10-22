using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Billing;

/// <summary>Owner store earnings ledger (distinct from AI credits): sales net of marketplace fee, payouts, adjustments.</summary>
public class OwnerEarningsService(AppDbContext db, IOptions<BillingOptions> billing)
{
    public decimal FeePercent => Math.Clamp(billing.Value.MarketplaceFeePercent, 0, 100);

    public static int ToMinor(decimal major) =>
        (int)Math.Round(major * 100m, MidpointRounding.AwayFromZero);

    public static decimal FromMinor(int minor) => minor / 100m;

    public (int FeeMinor, int NetMinor) Split(int grossMinor)
    {
        var fee = (int)Math.Round(grossMinor * (FeePercent / 100m), MidpointRounding.AwayFromZero);
        fee = Math.Clamp(fee, 0, grossMinor);
        return (fee, grossMinor - fee);
    }

    public async Task<OwnerEarningEntry?> CreditSaleAsync(Payment payment, Order order, CancellationToken ct = default)
    {
        if (payment.Kind != PaymentKinds.Store || payment.OrderId is null) return null;
        if (await db.OwnerEarningEntries.AnyAsync(e => e.PaymentId == payment.Id && e.Type == OwnerEarningTypes.Sale, ct))
            return null;

        var (fee, net) = Split(payment.AmountMinor);
        var entry = new OwnerEarningEntry
        {
            UserId = payment.UserId,
            ProjectId = payment.ProjectId ?? order.ProjectId,
            OrderId = order.Id,
            PaymentId = payment.Id,
            GrossMinor = payment.AmountMinor,
            FeeMinor = fee,
            NetMinor = net,
            Currency = payment.Currency,
            Type = OwnerEarningTypes.Sale,
            Note = $"طلب #{order.Number}"
        };
        db.OwnerEarningEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<object> SummaryForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await db.OwnerEarningEntries.AsNoTracking().Where(e => e.UserId == userId).ToListAsync(ct);
        return BuildSummary(rows);
    }

    public int AvailableMinor(IEnumerable<OwnerEarningEntry> rows) =>
        rows.Where(e => e.Type is OwnerEarningTypes.Sale or OwnerEarningTypes.Adjustment).Sum(e => e.NetMinor)
        - rows.Where(e => e.Type == OwnerEarningTypes.Payout).Sum(e => e.NetMinor);

    public object BuildSummary(IReadOnlyList<OwnerEarningEntry> rows)
    {
        var sales = rows.Where(e => e.Type == OwnerEarningTypes.Sale).ToList();
        var payouts = rows.Where(e => e.Type == OwnerEarningTypes.Payout).ToList();
        var adjustments = rows.Where(e => e.Type == OwnerEarningTypes.Adjustment).ToList();
        var currency = sales.Select(e => e.Currency).Concat(payouts.Select(e => e.Currency)).Concat(adjustments.Select(e => e.Currency))
            .GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "AED";

        var gross = sales.Sum(e => e.GrossMinor);
        var fees = sales.Sum(e => e.FeeMinor);
        var netSales = sales.Sum(e => e.NetMinor);
        var paidOut = payouts.Sum(e => e.NetMinor);
        var adj = adjustments.Sum(e => e.NetMinor);
        var available = AvailableMinor(rows);

        return new
        {
            currency,
            feePercent = FeePercent,
            available = FromMinor(available),
            availableMinor = available,
            grossSales = FromMinor(gross),
            fees = FromMinor(fees),
            netSales = FromMinor(netSales),
            paidOut = FromMinor(paidOut),
            adjustments = FromMinor(adj),
            salesCount = sales.Count,
            payoutCount = payouts.Count
        };
    }

    /// <summary>Admin payout (positive amount deducted from balance) or signed adjustment (positive credit / negative debit).</summary>
    public async Task<OwnerEarningEntry> RecordAdminAsync(Guid userId, string type, decimal signedAmount, string? currency, string? note, Guid? projectId = null, CancellationToken ct = default)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) throw ApiException.NotFound("المستخدم غير موجود");
        type = (type ?? "").Trim().ToLowerInvariant();
        if (type is not (OwnerEarningTypes.Payout or OwnerEarningTypes.Adjustment))
            throw ApiException.BadRequest("نوع القيد غير صالح");
        if (signedAmount == 0 || Math.Abs(signedAmount) > 10_000_000) throw ApiException.BadRequest("المبلغ غير صالح");
        var cur = Text.Truncate(string.IsNullOrWhiteSpace(currency) ? "AED" : currency.Trim().ToUpperInvariant(), 3);

        if (type == OwnerEarningTypes.Payout)
        {
            if (signedAmount < 0) throw ApiException.BadRequest("مبلغ الصرف يجب أن يكون موجباً");
            var rows = await db.OwnerEarningEntries.AsNoTracking().Where(e => e.UserId == userId).ToListAsync(ct);
            var minor = ToMinor(signedAmount);
            if (minor > AvailableMinor(rows)) throw ApiException.BadRequest("المبلغ أكبر من الرصيد المتاح");
            var entry = new OwnerEarningEntry
            {
                UserId = userId,
                ProjectId = projectId,
                NetMinor = minor,
                Currency = cur,
                Type = OwnerEarningTypes.Payout,
                Note = Text.Truncate(note?.Trim(), 500)
            };
            db.OwnerEarningEntries.Add(entry);
            await db.SaveChangesAsync(ct);
            return entry;
        }

        var adj = new OwnerEarningEntry
        {
            UserId = userId,
            ProjectId = projectId,
            GrossMinor = Math.Abs(ToMinor(signedAmount)),
            NetMinor = ToMinor(signedAmount),
            Currency = cur,
            Type = OwnerEarningTypes.Adjustment,
            Note = Text.Truncate(note?.Trim(), 500)
        };
        db.OwnerEarningEntries.Add(adj);
        await db.SaveChangesAsync(ct);
        return adj;
    }
}
