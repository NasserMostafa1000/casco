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

public class WalletTransferService(AppDbContext db, SubscriptionService subscriptions, WalletProofStore proofs)
{
    public const int MaxBytes = 4 * 1024 * 1024;

    public async Task<WalletTransfer> SubmitAsync(Guid userId, string method, Stream body, CancellationToken ct)
    {
        if (!WalletMethods.IsValid(method))
            throw ApiException.BadRequest("اختَر فودافون كاش أو إنستا باي");
        if (await db.WalletTransfers.AnyAsync(t => t.UserId == userId && t.Status == WalletTransferStatuses.Pending, ct))
            throw ApiException.Conflict("عندك تحويل قيد المراجعة. استنى الموافقة أو الرفض قبل ما تبعت صورة جديدة.", "wallet_pending");

        await using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, ct);
        if (buffer.Length is 0 or > MaxBytes) throw ApiException.BadRequest("ارفع صورة التحويل (حد أقصى 4 ميجا)");
        var bytes = buffer.ToArray();
        var ext = ImageExtension(bytes) ?? throw ApiException.BadRequest("الصورة لازم تكون JPG أو PNG أو WebP");

        var row = new WalletTransfer
        {
            UserId = userId,
            Method = method,
            AmountMinor = WalletPay.AmountMinor,
            Currency = WalletPay.Currency,
            ContentType = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : "image/jpeg"
        };
        row.ProofKey = await proofs.SaveAsync(row.Id, new MemoryStream(bytes), ext, ct);
        db.WalletTransfers.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task ApproveAsync(Guid id)
    {
        var row = await db.WalletTransfers.FirstOrDefaultAsync(t => t.Id == id)
                  ?? throw ApiException.NotFound("التحويل غير موجود");
        if (row.Status == WalletTransferStatuses.Approved) return;
        if (row.Status != WalletTransferStatuses.Pending)
            throw ApiException.BadRequest("التحويل ده اتراجع قبل كده");
        row.Status = WalletTransferStatuses.Approved;
        row.ReviewedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await subscriptions.ActivateProAsync(row.UserId, BillingIntervals.Monthly, row.Id.ToString());
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
