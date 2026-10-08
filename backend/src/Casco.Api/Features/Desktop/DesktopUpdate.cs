using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Casco.Api.Features.Desktop;

public static class DesktopUpdate
{
    public const string SettingKey = "desktop.update";
    public const string ClientHeader = "X-Casco-Version";
    public const string DefaultMessage = "فيه تحديث مطلوب لـ Casco Studio. نزّل آخر نسخة عشان تكمل استخدام البرنامج.";

    public sealed record Policy(string Version, string Message);

    public static async Task<Policy> LoadAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.Value))
            return new Policy("0.2.0", "");
        try
        {
            var policy = JsonSerializer.Deserialize<Policy>(row.Value, JsonSerializerOptions.Web);
            var version = (policy?.Version ?? "").Trim();
            if (version.Length == 0) version = "0.2.0";
            return new Policy(version, (policy?.Message ?? "").Trim());
        }
        catch (JsonException)
        {
            return new Policy("0.2.0", "");
        }
    }

    public static async Task SaveAsync(AppDbContext db, string version, string message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new Policy(version.Trim(), message.Trim()), JsonSerializerOptions.Web);
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row is null)
            db.AppSettings.Add(new AppSetting { Key = SettingKey, Value = json, UpdatedAt = DateTime.UtcNow });
        else
        {
            row.Value = json;
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public static async Task EnsureCurrentAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var client = http.Request.Headers[ClientHeader].ToString();
        if (string.IsNullOrWhiteSpace(client)) return;
        var policy = await LoadAsync(db, ct);
        if (!IsOlder(client, policy.Version)) return;
        var message = string.IsNullOrWhiteSpace(policy.Message) ? DefaultMessage : policy.Message;
        throw new ApiException(426, message, "update_required");
    }

    public static bool IsOlder(string? client, string required)
    {
        if (string.IsNullOrWhiteSpace(required)) return false;
        if (string.IsNullOrWhiteSpace(client)) return true;
        var left = Parts(client);
        var right = Parts(required);
        var count = Math.Max(left.Length, right.Length);
        for (var i = 0; i < count; i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;
            if (a < b) return true;
            if (a > b) return false;
        }
        return false;
    }

    private static int[] Parts(string version)
    {
        var numbers = new List<int>();
        foreach (var piece in version.Split('.', '-', '+'))
        {
            var digits = new string(piece.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) continue;
            numbers.Add(int.TryParse(digits, out var value) ? value : 0);
        }
        return numbers.Count == 0 ? [0] : numbers.ToArray();
    }
}
