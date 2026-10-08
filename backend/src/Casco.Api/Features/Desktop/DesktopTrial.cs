using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Casco.Api.Features.Desktop;

/// <summary>
/// A new computer gets one free prompt, then five hours after the download link is shared in five groups.
/// The hours stay on the machine. A subscribed account is not limited by this.
/// </summary>
public static class DesktopTrial
{
    public const int Groups = 5;
    public const string MachineHeader = "X-Casco-Machine";
    public const string DownloadUrl = "https://casco.studio/download";
    public static readonly TimeSpan Duration = TimeSpan.FromHours(5);

    public sealed record Access(
        bool IsPro,
        bool ModelLocked,
        bool ShareRequired,
        bool FreePrompt,
        bool Trial,
        int Shares,
        DateTime? TrialEndsAt,
        int Mask);

    public static string? Hash(string? machine)
    {
        var value = (machine ?? "").Trim().ToLowerInvariant();
        if (value.Length is < 32 or > 80) return null;
        foreach (var ch in value)
        {
            if (!char.IsAsciiHexDigit(ch) && ch != '-') return null;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static int CountShares(int mask)
    {
        var count = 0;
        for (var bit = 0; bit < Groups; bit++)
        {
            if ((mask & (1 << bit)) != 0) count++;
        }
        return count;
    }

    public static async Task<Access> ReadAsync(AppDbContext db, Guid userId, bool isPro, string? machine, CancellationToken ct)
    {
        if (isPro) return new Access(true, false, false, false, false, 0, null, 0);
        var hash = Hash(machine);
        if (hash is null) return new Access(false, false, false, false, false, 0, null, 0);
        var row = await LoadAsync(db, hash, userId, ct);
        return Describe(row, DateTime.UtcNow);
    }

    public static async Task<Access> ShareAsync(AppDbContext db, Guid userId, bool isPro, string? machine, int slot, CancellationToken ct)
    {
        if (isPro) return new Access(true, false, false, false, false, 0, null, 0);
        if (slot is < 1 or > Groups) throw ApiException.BadRequest("اختر جروب من 1 إلى 5");
        var hash = Hash(machine) ?? throw ApiException.BadRequest("الجهاز مش متعرّف");
        var row = await LoadAsync(db, hash, userId, ct);
        if (row.TrialEndsAt is null)
        {
            row.ShareMask |= 1 << (slot - 1);
            if (CountShares(row.ShareMask) >= Groups && row.FreePromptUsed)
                row.TrialEndsAt = DateTime.UtcNow.Add(Duration);
            await db.SaveChangesAsync(ct);
        }
        return Describe(row, DateTime.UtcNow);
    }

    public static async Task MarkFreePromptUsedAsync(AppDbContext db, string? machine, CancellationToken ct)
    {
        var hash = Hash(machine);
        if (hash is null) return;
        var row = await db.DesktopMachines.FirstOrDefaultAsync(m => m.MachineHash == hash, ct);
        if (row is null || row.FreePromptUsed) return;
        row.FreePromptUsed = true;
        await db.SaveChangesAsync(ct);
    }

    public static async Task LogAsync(AppDbContext db, Guid userId, string? machine, string model, string? text, CancellationToken ct)
    {
        var clean = (text ?? "").Replace('\0', ' ').Trim();
        if (clean.Length == 0) return;
        if (clean.Length > 2000) clean = clean[..2000];
        db.DesktopChatMessages.Add(new DesktopChatMessage
        {
            UserId = userId,
            MachineHash = Hash(machine) ?? "",
            Model = model.Length > 80 ? model[..80] : model,
            Text = clean,
        });
        await db.SaveChangesAsync(ct);
    }

    public static string PromptText(JsonElement body)
    {
        if (body.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
        {
            var text = (prompt.GetString() ?? "").Trim();
            if (text.Length > 0) return text;
        }
        return "";
    }

    private static Access Describe(DesktopMachine row, DateTime now)
    {
        var trial = row.TrialEndsAt is { } end && end > now;
        var freePrompt = !row.FreePromptUsed && row.TrialEndsAt is null;
        var shareRequired = row.FreePromptUsed && row.TrialEndsAt is null;
        return new Access(false, trial || freePrompt, shareRequired, freePrompt, trial, CountShares(row.ShareMask), row.TrialEndsAt, row.ShareMask);
    }

    private static async Task<DesktopMachine> LoadAsync(AppDbContext db, string hash, Guid userId, CancellationToken ct)
    {
        var row = await db.DesktopMachines.FirstOrDefaultAsync(m => m.MachineHash == hash, ct);
        if (row is not null)
        {
            row.LastUserId = userId;
            return row;
        }
        row = new DesktopMachine { MachineHash = hash, LastUserId = userId };
        db.DesktopMachines.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }
}
