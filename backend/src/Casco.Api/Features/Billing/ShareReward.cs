using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Casco.Api.Features.Billing;

/// <summary>Two extra free prompts after ten real signups from the user's invite link.</summary>
public static class ShareReward
{
    public const int FreePrompts = 1;
    public const int Friends = 10;
    public const int BonusPrompts = 2;

    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public static async Task<string> EnsureCodeAsync(AppDbContext db, User user)
    {
        if (!string.IsNullOrEmpty(user.ReferralCode)) return user.ReferralCode;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var chars = new char[8];
            for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
            var code = new string(chars);
            if (await db.Users.AnyAsync(u => u.ReferralCode == code)) continue;
            user.ReferralCode = code;
            await db.SaveChangesAsync();
            return code;
        }
        throw new InvalidOperationException("Could not allocate a referral code");
    }

    /// <summary>Links a brand-new account to the inviter. Existing accounts and unknown codes are ignored.</summary>
    public static async Task AttachReferrerAsync(AppDbContext db, User user, string? code)
    {
        var referral = (code ?? "").Trim().ToLowerInvariant();
        if (referral.Length is < 6 or > 12) return;
        var referrerId = await db.Users.Where(u => u.ReferralCode == referral).Select(u => (Guid?)u.Id).FirstOrDefaultAsync();
        if (referrerId is null || referrerId == user.Id) return;
        user.ReferredByUserId = referrerId;
    }

    public static Task<int> JoinedAsync(AppDbContext db, Guid userId) =>
        db.Users.CountAsync(u => u.ReferredByUserId == userId);
}
