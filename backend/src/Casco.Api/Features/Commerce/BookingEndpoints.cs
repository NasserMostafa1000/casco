using System.Globalization;
using Casco.Api.Domain;
using Casco.Api.Features.Backend;
using Casco.Api.Features.Notifications;
using Casco.Api.Features.Projects;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Casco.Api.Features.Commerce;

public record BookRequest(Guid ServiceId, DateTime Start, string CustomerName, string Phone, string? Email, string? Notes, string? _hp = null);
public record BookingServiceRequest(string Name, string? Description, int DurationMinutes, decimal Price, bool IsActive = true, int SortOrder = 0);

/// <summary>Appointments: clinics, salons, consultants, courts, tables…</summary>
public static class BookingEndpoints
{
    public static void MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var s = app.MapGroup("/api/s/{siteKey}/bookings").RequireCors("sites").RequireRateLimiting("site");

        s.MapGet("/services", async (string siteKey, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var items = await site.Db.BookingServices.AsNoTracking().Where(x => x.ProjectId == project.Id && x.IsActive)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                .Select(x => new { x.Id, x.Name, x.Description, x.DurationMinutes, x.Price }).ToListAsync();
            var b = project.Settings.Booking;
            return Results.Ok(new { items, currency = project.Settings.Currency, timeZone = b.TimeZone, daysAhead = b.DaysAhead });
        });

        s.MapGet("/slots", async (string siteKey, Guid serviceId, string? date, HttpContext http, SiteContext site, TimeProvider clock) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var service = await site.Db.BookingServices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == serviceId && x.ProjectId == project.Id && x.IsActive)
                          ?? throw ApiException.NotFound("الخدمة غير موجودة");
            var settings = project.Settings.Booking;
            var now = clock.GetUtcNow().UtcDateTime;
            var day = ParseDate(date) ?? BookingSlots.Today(settings, now);
            var slots = await SlotsAsync(site.Db, project.Id, settings, service.DurationMinutes, day, now);
            return Results.Ok(new
            {
                date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                items = slots.Select(x => new { start = x.StartUtc, end = x.EndUtc, time = x.LocalTime, x.Available })
            });
        });

        s.MapPost("", async (string siteKey, BookRequest req, HttpContext http, SiteContext site, SiteNotifier notifier, TimeProvider clock) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            if (!string.IsNullOrEmpty(req._hp)) return Results.Ok(new { ok = true });
            var db = site.Db;
            var name = StoreEndpoints.RequireText(req.CustomerName, "الاسم", 120);
            var phone = StoreEndpoints.RequirePhone(req.Phone);
            var service = await db.BookingServices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.ServiceId && x.ProjectId == project.Id && x.IsActive)
                          ?? throw ApiException.NotFound("الخدمة غير موجودة");
            var settings = project.Settings.Booking;
            var start = req.Start.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(req.Start, DateTimeKind.Utc) : req.Start.ToUniversalTime();
            var userId = await site.SiteUserIdAsync(http, project);

            var gate = RecordStore.LockFor(project.Id);
            await gate.WaitAsync();
            Booking booking;
            try
            {
                await site.EnsureTrialCapacityAsync(project, () => db.Bookings.CountAsync(b => b.ProjectId == project.Id));
                var now = clock.GetUtcNow().UtcDateTime;
                var slots = await SlotsAsync(db, project.Id, settings, service.DurationMinutes, BookingSlots.LocalDate(settings, start), now);
                var slot = slots.FirstOrDefault(x => x.StartUtc == start);
                if (slot is null) throw ApiException.BadRequest("هذا الموعد غير متاح، اختر موعداً آخر", "slot_invalid");
                if (!slot.Available) throw ApiException.Conflict("تم حجز هذا الموعد للتو، اختر موعداً آخر", "slot_taken");
                booking = new Booking
                {
                    ProjectId = project.Id,
                    ServiceId = service.Id,
                    StartUtc = slot.StartUtc,
                    EndUtc = slot.EndUtc,
                    SiteUserId = userId,
                    CustomerName = name,
                    Phone = phone,
                    Email = StoreEndpoints.OptionalEmail(req.Email),
                    Notes = StoreEndpoints.OptionalText(req.Notes, 1000),
                    Status = settings.AutoConfirm ? BookingStatuses.Confirmed : BookingStatuses.Pending
                };
                db.Bookings.Add(booking);
                await db.SaveChangesAsync();
            }
            finally { gate.Release(); }

            var summary = Summary(booking, service.Name, settings);
            notifier.Notify(project, SiteEventKind.Booking, summary);
            return Results.Ok(new
            {
                booking.Id, booking.Status, start = booking.StartUtc, end = booking.EndUtc, service = service.Name,
                localTime = LocalText(booking.StartUtc, settings),
                whatsappUrl = SiteContext.WhatsAppLink(project.Settings.WhatsApp, summary)
            });
        }).RequireRateLimiting("forms");

        s.MapGet("/my", async (string siteKey, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var user = await site.RequireUserAsync(http, project);
            var db = site.Db;
            var rows = await (from b in db.Bookings
                              join sv in db.BookingServices on b.ServiceId equals sv.Id
                              where b.ProjectId == project.Id && b.SiteUserId == user.Id
                              orderby b.StartUtc descending
                              select new { b.Id, b.Status, start = b.StartUtc, end = b.EndUtc, service = sv.Name }).Take(100).ToListAsync();
            return Results.Ok(new { items = rows });
        });

        // ---------- Owner dashboard ----------
        var d = app.MapGroup("/api/projects/{id:guid}/data").RequireAuthorization();

        d.MapGet("/booking-services", async (Guid id, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            return Results.Ok(await db.BookingServices.Where(x => x.ProjectId == id).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync());
        });

        d.MapPost("/booking-services", async (Guid id, BookingServiceRequest req, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            if (await db.BookingServices.CountAsync(x => x.ProjectId == id) >= 200) throw ApiException.BadRequest("وصلت للحد الأقصى من الخدمات");
            var service = new BookingService { ProjectId = id };
            Apply(service, req);
            db.BookingServices.Add(service);
            await db.SaveChangesAsync();
            return Results.Ok(new { service.Id });
        });

        d.MapPut("/booking-services/{sid:guid}", async (Guid id, Guid sid, BookingServiceRequest req, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var service = await db.BookingServices.FirstOrDefaultAsync(x => x.Id == sid && x.ProjectId == id) ?? throw ApiException.NotFound();
            Apply(service, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        d.MapDelete("/booking-services/{sid:guid}", async (Guid id, Guid sid, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            if (await db.Bookings.AnyAsync(b => b.ServiceId == sid && b.ProjectId == id))
                await db.BookingServices.Where(x => x.Id == sid && x.ProjectId == id).ExecuteUpdateAsync(x => x.SetProperty(v => v.IsActive, false));
            else
                await db.BookingServices.Where(x => x.Id == sid && x.ProjectId == id).ExecuteDeleteAsync();
            return Results.NoContent();
        });

        d.MapGet("/bookings", async (Guid id, string? status, bool? upcoming, HttpContext http, AppDbContext db, ProjectService projects, TimeProvider clock) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            var settings = SiteSettings.Parse(project.SettingsJson).Booking;
            var now = clock.GetUtcNow().UtcDateTime;
            var query = from b in db.Bookings
                        join sv in db.BookingServices on b.ServiceId equals sv.Id
                        where b.ProjectId == id
                        select new { b, service = sv.Name };
            if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.b.Status == status);
            if (upcoming == true) query = query.Where(x => x.b.EndUtc >= now);
            var rows = await (upcoming == true ? query.OrderBy(x => x.b.StartUtc) : query.OrderByDescending(x => x.b.StartUtc)).Take(300).ToListAsync();
            return Results.Ok(new
            {
                pendingCount = await db.Bookings.CountAsync(b => b.ProjectId == id && b.Status == BookingStatuses.Pending && b.EndUtc >= now),
                items = rows.Select(x => new
                {
                    x.b.Id, x.service, start = x.b.StartUtc, end = x.b.EndUtc, localTime = LocalText(x.b.StartUtc, settings),
                    x.b.CustomerName, x.b.Phone, x.b.Email, x.b.Notes, x.b.Status, x.b.CreatedAt,
                    whatsappUrl = SiteContext.WhatsAppLink(x.b.Phone, $"بخصوص حجزك ({x.service}) يوم {LocalText(x.b.StartUtc, settings)}")
                })
            });
        });

        d.MapPost("/bookings/{bid:guid}/status", async (Guid id, Guid bid, StatusRequest req, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            if (!BookingStatuses.All.Contains(req.Status)) throw ApiException.BadRequest("حالة غير صالحة");
            var updated = await db.Bookings.Where(b => b.Id == bid && b.ProjectId == id).ExecuteUpdateAsync(x => x.SetProperty(b => b.Status, req.Status));
            return updated == 0 ? throw ApiException.NotFound() : Results.NoContent();
        });
    }

    private static async Task<List<BookingSlot>> SlotsAsync(AppDbContext db, Guid projectId, BookingSettings settings, int duration, DateOnly day, DateTime now)
    {
        var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-1);
        var to = from.AddDays(3);
        var busy = await db.Bookings.AsNoTracking()
            .Where(b => b.ProjectId == projectId && b.Status != BookingStatuses.Canceled && b.StartUtc < to && b.EndUtc > from)
            .Select(b => new { b.StartUtc, b.EndUtc }).ToListAsync();
        return BookingSlots.Compute(settings, duration, day, busy.Select(b => (b.StartUtc, b.EndUtc)), now);
    }

    private static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string LocalText(DateTime utc, BookingSettings s)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), BookingSlots.Zone(s));
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Summary(Booking b, string service, BookingSettings s) =>
        $"حجز جديد: {service}\nالموعد: {LocalText(b.StartUtc, s)}\nالاسم: {b.CustomerName}\nالهاتف: {b.Phone}" +
        (string.IsNullOrEmpty(b.Notes) ? "" : $"\nملاحظات: {b.Notes}");

    private static void Apply(BookingService x, BookingServiceRequest r)
    {
        x.Name = StoreEndpoints.RequireText(r.Name, "اسم الخدمة", 150);
        x.Description = Text.Truncate(r.Description?.Trim(), 2000);
        x.DurationMinutes = Math.Clamp(r.DurationMinutes <= 0 ? 30 : r.DurationMinutes, 5, 60 * 12);
        if (r.Price < 0) throw ApiException.BadRequest("السعر غير صالح");
        x.Price = r.Price;
        x.IsActive = r.IsActive;
        x.SortOrder = r.SortOrder;
    }
}
