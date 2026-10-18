using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Billing;

public record WalletReview(string? Note);

/// <summary>Saves transfer screenshots outside the public upload CDN.</summary>
public class WalletProofStore(IOptions<AppOptions> options)
{
    private readonly string _root = Path.GetFullPath(Path.Combine(options.Value.DataPath, "wallet-proofs"));

    public async Task<string> SaveAsync(Guid id, Stream content, string extension, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        var name = id.ToString("N") + extension;
        var full = Path.Combine(_root, name);
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Invalid proof name");
        await using var target = File.Create(full);
        await content.CopyToAsync(target, ct);
        return name;
    }

    public Stream? Open(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('/') || key.Contains('\\') || key.Contains("..")) return null;
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) return null;
        return File.OpenRead(full);
    }
}

public record WalletApprove(string? Interval);

public class WalletTransferService(AppDbContext db, SubscriptionService subscriptions, WalletProofStore proofs, HostingService? hosting = null)
{
    public const int MaxBytes = 4 * 1024 * 1024;

    public async Task<WalletTransfer> SubmitAsync(Guid userId, string method, Stream body, CancellationToken ct, string? interval = null)
    {
        if (!WalletMethods.IsValid(method))
            throw ApiException.BadRequest("اختَر فودافون كاش أو إنستا باي");
        if (await db.WalletTransfers.AnyAsync(t => t.UserId == userId && t.Status == WalletTransferStatuses.Pending && t.Purpose != WalletPurposes.Server, ct))
            throw ApiException.Conflict("عندك تحويل قيد المراجعة. استنى الموافقة أو الرفض قبل ما تبعت صورة جديدة.", "wallet_pending");

        var period = interval == BillingIntervals.Yearly ? BillingIntervals.Yearly : BillingIntervals.Monthly;
        var row = await StoreAsync(userId, method, WalletPurposes.Pro, null, null, WalletPay.ForInterval(period), WalletPay.Currency, body, ct, period);
        return row;
    }

    public async Task<WalletTransfer> SubmitServerAsync(Guid userId, Guid projectId, string method, Stream body, CancellationToken ct, string? interval = null)
    {
        if (hosting is null) throw ApiException.BadRequest("دفع السيرفر غير متاح");
        if (!WalletMethods.IsValid(method))
            throw ApiException.BadRequest("اختَر فودافون كاش أو إنستا باي");
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, ct)
                      ?? throw ApiException.NotFound("الموقع غير موجود");
        Casco.Api.Features.Admin.SiteSuspension.EnsureNotSuspended(project);
        if (await db.WalletTransfers.AnyAsync(t => t.UserId == userId && t.ProjectId == projectId && t.Purpose == WalletPurposes.Server && t.Status == WalletTransferStatuses.Pending, ct))
            throw ApiException.Conflict("تحويل السيرفر ده قيد المراجعة.", "wallet_pending");
        var tier = project.TemplateKey == HostingTiers.React ? HostingTiers.React : await hosting.RequiredTierAsync(project);
        var period = interval == BillingIntervals.Yearly || tier == HostingTiers.React ? BillingIntervals.Yearly : BillingIntervals.Monthly;
        return await StoreAsync(userId, method, WalletPurposes.Server, project.Id, tier, ServerWalletPay.ForInterval(period), ServerWalletPay.Currency, body, ct, period);
    }

    private async Task<WalletTransfer> StoreAsync(Guid userId, string method, string purpose, Guid? projectId, string? tier, int amountMinor, string currency, Stream body, CancellationToken ct, string? interval = null)
    {
        await using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, ct);
        if (buffer.Length is 0 or > MaxBytes) throw ApiException.BadRequest("ارفع صورة التحويل (حد أقصى 4 ميجا)");
        var bytes = buffer.ToArray();
        var ext = ImageExtension(bytes) ?? throw ApiException.BadRequest("الصورة لازم تكون JPG أو PNG أو WebP");

        var row = new WalletTransfer
        {
            UserId = userId,
            Method = method,
            Purpose = purpose,
            ProjectId = projectId,
            HostingTier = tier,
            Interval = interval,
            AmountMinor = amountMinor,
            Currency = currency,
            ContentType = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : "image/jpeg"
        };
        row.ProofKey = await proofs.SaveAsync(row.Id, new MemoryStream(bytes), ext, ct);
        db.WalletTransfers.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<WalletTransfer> ApproveAsync(Guid id, string? interval = null)
    {
        var row = await db.WalletTransfers.FirstOrDefaultAsync(t => t.Id == id)
                  ?? throw ApiException.NotFound("التحويل غير موجود");
        if (row.Status == WalletTransferStatuses.Approved) return row;
        if (row.Status != WalletTransferStatuses.Pending)
            throw ApiException.BadRequest("التحويل ده اتراجع قبل كده");
        if (row.Purpose == WalletPurposes.Server)
        {
            var period = row.Interval == BillingIntervals.Yearly ? BillingIntervals.Yearly : interval;
            if (period is not (BillingIntervals.Weekly or BillingIntervals.Monthly or BillingIntervals.Yearly))
                throw ApiException.BadRequest("اختَر تفعيل أسبوعي أو شهري أو سنوي");
            if (row.ProjectId is null || string.IsNullOrEmpty(row.HostingTier))
                throw ApiException.BadRequest("تحويل السيرفر ناقص الموقع");
            if (hosting is null) throw ApiException.BadRequest("دفع السيرفر غير متاح");
            row.Interval = period;
            await hosting.GrantAsync(row.ProjectId.Value, row.HostingTier, period == BillingIntervals.Monthly, period == BillingIntervals.Yearly);
        }
        row.Status = WalletTransferStatuses.Approved;
        row.ReviewedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (row.Purpose != WalletPurposes.Server)
            await subscriptions.ActivateProAsync(row.UserId, row.Interval == BillingIntervals.Yearly ? BillingIntervals.Yearly : BillingIntervals.Monthly, row.Id.ToString());
        return row;
    }

    public async Task RejectAsync(Guid id, string? note)
    {
        var row = await db.WalletTransfers.FirstOrDefaultAsync(t => t.Id == id)
                  ?? throw ApiException.NotFound("التحويل غير موجود");
        if (row.Status != WalletTransferStatuses.Pending)
            throw ApiException.BadRequest("التحويل ده اتراجع قبل كده");
        row.Status = WalletTransferStatuses.Rejected;
        row.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 300)];
        row.ReviewedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public static string? ImageExtension(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return ".png";
        if (head.Length >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
            && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P') return ".webp";
        return null;
    }
}
