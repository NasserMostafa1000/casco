using System.Globalization;
using Casco.Api.Features.SiteRuntime;

namespace Casco.Api.Features.Commerce;

public record BookingSlot(DateTime StartUtc, DateTime EndUtc, string LocalTime, bool Available);

/// <summary>Computes bookable time slots from the site's working hours, capacity and existing bookings.</summary>
public static class BookingSlots
{
    public static TimeZoneInfo Zone(BookingSettings s)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }

    public static DateOnly Today(BookingSettings s, DateTime nowUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, Zone(s)));

    public static DateOnly LocalDate(BookingSettings s, DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone(s)));

    public static List<BookingSlot> Compute(BookingSettings s, int durationMinutes, DateOnly date,
        IEnumerable<(DateTime Start, DateTime End)> existing, DateTime nowUtc)
    {
        var result = new List<BookingSlot>();
        var today = Today(s, nowUtc);
        if (date < today || date > today.AddDays(s.DaysAhead)) return result;
        if (s.ClosedDates.Contains(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) return result;
        var day = s.Week.FirstOrDefault(w => w.Day == (int)date.DayOfWeek);
        if (day is null || day.Closed) return result;
        if (!TimeOnly.TryParse(day.Open, CultureInfo.InvariantCulture, out var open) ||
            !TimeOnly.TryParse(day.Close, CultureInfo.InvariantCulture, out var close)) return result;

        var zone = Zone(s);
        var busy = existing.ToList();
        var duration = TimeSpan.FromMinutes(Math.Max(5, durationMinutes));
        var step = TimeSpan.FromMinutes(Math.Max(5, s.SlotMinutes));
        var earliest = nowUtc.AddMinutes(s.MinNoticeMinutes);
        var dayStart = date.ToDateTime(TimeOnly.MinValue);

        for (var t = open.ToTimeSpan(); t + duration <= close.ToTimeSpan(); t += step)
        {
            var local = DateTime.SpecifyKind(dayStart + t, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) continue;
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
            if (startUtc < earliest) continue;
            var endUtc = startUtc + duration;
            var overlapping = busy.Count(b => b.Start < endUtc && b.End > startUtc);
            result.Add(new BookingSlot(startUtc, endUtc, local.ToString("HH:mm", CultureInfo.InvariantCulture), overlapping < Math.Max(1, s.Capacity)));
        }
        return result;
    }
}
