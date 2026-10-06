using Casco.Api.Domain;
using Casco.Api.Features.Billing;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Casco.Api.Features.Admin;

public record SuspendSiteRequest(string? Reason);

/// <summary>
/// Admin suspension is a moderation decision kept apart from hosting payment: it takes priority over paid hosting
/// and lifting it leaves the hosting dates untouched.
/// </summary>
public class SiteSuspension(AppDbContext db, HostingService hosting, TimeProvider clock, ILogger<SiteSuspension> logger)
{
    public const string Code = "site_suspended";
    public const int MaxReason = 500;

    /// <summary>For the owner: explains why and how to reach support.</summary>
    public static ApiException OwnerError(string? reason) =>
        new(403, $"هذا الموقع موقوف من إدارة Casco{(string.IsNullOrWhiteSpace(reason) ? "" : $" (السبب: {reason})")}. للاستفسار تواصل مع الدعم على واتساب {SiteDataEndpoints.SupportWhatsApp}.", Code);

    /// <summary>For visitors: the reason stays private.</summary>
    public static ApiException VisitorError() => new(403, "هذا الموقع متوقف مؤقتاً", Code);

    public static void EnsureNotSuspended(Project project)
    {
        if (project.IsAdminSuspended) throw OwnerError(project.AdminSuspensionReason);
    }

    public async Task SuspendAsync(Guid projectId, string? reason, Guid adminId)
    {
        var text = (reason ?? "").Trim();
        if (text.Length < 3) throw ApiException.BadRequest("اكتب سبب الإيقاف");
        if (text.Length > MaxReason) throw ApiException.BadRequest($"سبب الإيقاف أطول من {MaxReason} حرف");
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId) ?? throw ApiException.NotFound("الموقع غير موجود");
        project.IsAdminSuspended = true;
        project.AdminSuspensionReason = text;
        project.AdminSuspendedAt = clock.GetUtcNow().UtcDateTime;
        project.AdminSuspendedBy = adminId;
        await db.SaveChangesAsync();
        hosting.Invalidate(project);
        logger.LogWarning("Site {ProjectId} suspended by admin {AdminId}: {Reason}", projectId, adminId, text);
    }

    public async Task UnsuspendAsync(Guid projectId, Guid adminId)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId) ?? throw ApiException.NotFound("الموقع غير موجود");
        if (!project.IsAdminSuspended) return;
        project.IsAdminSuspended = false;
        project.AdminSuspensionReason = null;
        project.AdminSuspendedAt = null;
        project.AdminSuspendedBy = null;
        await db.SaveChangesAsync();
        hosting.Invalidate(project);
        logger.LogWarning("Site {ProjectId} unsuspended by admin {AdminId}", projectId, adminId);
    }
}
