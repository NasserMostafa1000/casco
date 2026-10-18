using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Casco.Api.Features.Auth;

/// <summary>One complimentary account: Pro features and no credit limit.</summary>
public static class UnlimitedAccount
{
    public const string Email = "rosanahossam@gmail.com";
    public const string OwnerEmail = "nasermostafa.ma122@gmail.com";

    public static bool Matches(string? email) =>
        string.Equals(Text.NormalizeEmail(email ?? ""), Email, StringComparison.Ordinal);

    public static Task<bool> IsUserAsync(AppDbContext db, Guid userId, CancellationToken ct = default) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.Email == Email, ct);
}
