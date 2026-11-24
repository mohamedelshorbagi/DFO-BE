using Dfo.Domain;

namespace Dfo.Application;

public record Slot(DateTime StartsAtUtc, DateTime EndsAtUtc, bool Available, string? UnavailableReason);

public static class ViewingRules
{
    public static readonly TimeZoneInfo Cairo = ResolveCairo();

    private static TimeZoneInfo ResolveCairo()
    {
        foreach (var id in new[] { "Africa/Cairo", "Egypt Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        throw new InvalidOperationException("Cairo time zone not available.");
    }

    public static DateTime CairoToUtc(DateOnly d, TimeOnly t) =>
        TimeZoneInfo.ConvertTimeToUtc(d.ToDateTime(t, DateTimeKind.Unspecified), Cairo);

    /// <summary>
    /// Builds slots for one Cairo calendar day from weekly rules, honouring exceptions,
    /// existing bookings and the current time.
    /// </summary>
    public static List<Slot> GenerateSlots(
        DateOnly date,
        IEnumerable<AvailabilityRule> rules,
        IEnumerable<AvailabilityException> exceptions,
        IEnumerable<(DateTime Start, DateTime End)> booked,
        DateTime nowUtc)
    {
        var ex = exceptions.FirstOrDefault(e => e.Date == date);
        var windows = new List<(TimeOnly Start, TimeOnly End, int Dur)>();

        if (ex is { Blocked: true })
            return new List<Slot>();   // whole day blocked: nothing offered

        if (ex is { Blocked: false, StartTime: { } s, EndTime: { } e })
            windows.Add((s, e, ex.SlotDurationMin ?? 30));
        else
            windows.AddRange(rules.Where(r => r.DayOfWeek == (int)date.DayOfWeek)
                                  .Select(r => (r.StartTime, r.EndTime, r.SlotDurationMin)));

        var slots = new List<Slot>();
        foreach (var (start, end, dur) in windows)
        {
            if (dur <= 0) continue;
            for (var t = start; t.AddMinutes(dur) <= end && t.AddMinutes(dur) > t; t = t.AddMinutes(dur))
            {
                var s0 = CairoToUtc(date, t);
                var s1 = s0.AddMinutes(dur);
                string? reason = null;
                if (s0 <= nowUtc) reason = "past";
                else if (booked.Any(b => b.Start < s1 && s0 < b.End)) reason = "booked";
                slots.Add(new Slot(s0, s1, reason is null, reason));
            }
        }
        return slots.OrderBy(x => x.StartsAtUtc).ToList();
    }

    /// <summary>
    /// Weekly hours offered when an owner hasn't configured any (Sun-Thu 14:00-18:00, Fri-Sat 11:00-18:00, 60-minute slots),
    /// so a listing is bookable from day one. Owners replace these by saving their own schedule.
    /// </summary>
    public static List<AvailabilityRule> DefaultRules(string ownerId, Guid? propertyId = null) =>
        Enumerable.Range(0, 7).Select(d => new AvailabilityRule
        {
            OwnerId = ownerId, PropertyId = propertyId, DayOfWeek = d,
            StartTime = d is 5 or 6 ? new TimeOnly(11, 0) : new TimeOnly(14, 0),
            EndTime = new TimeOnly(18, 0), SlotDurationMin = 60
        }).ToList();

    public static bool IsActive(ViewingStatus s) =>
        s is ViewingStatus.Requested or ViewingStatus.Confirmed or ViewingStatus.RescheduleRequested;

    /// <summary>Legal state transitions. Actor checks live in the controller.</summary>
    public static bool CanTransition(ViewingStatus from, ViewingStatus to) => (from, to) switch
    {
        (ViewingStatus.Requested, ViewingStatus.Confirmed) => true,
        (ViewingStatus.Requested, ViewingStatus.Declined) => true,
        (ViewingStatus.Requested, ViewingStatus.Cancelled) => true,
        (ViewingStatus.Confirmed, ViewingStatus.RescheduleRequested) => true,
        (ViewingStatus.Confirmed, ViewingStatus.Cancelled) => true,
        (ViewingStatus.Confirmed, ViewingStatus.Completed) => true,
        (ViewingStatus.RescheduleRequested, ViewingStatus.Confirmed) => true,   // approve or decline reschedule
        (ViewingStatus.RescheduleRequested, ViewingStatus.Cancelled) => true,
        _ => false
    };
}
