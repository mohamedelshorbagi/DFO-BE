using System.Security.Claims;
using Dfo.Application;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Authorize]
[Route("api/v1")]
public class ViewingsController(DfoDbContext db, ChatShaper people, MediaUrlSigner signer) : ControllerBase
{
    private string Uid => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ---------- Availability (owner) ----------
    public record RuleDto(int DayOfWeek, string StartTime, string EndTime, int SlotDurationMin);
    public record ExceptionDto(string Date, string Type, string? StartTime, string? EndTime, int? SlotDurationMin, string? Reason);
    public record AvailabilityDto(List<RuleDto> Rules, List<ExceptionDto> Exceptions);

    [HttpGet("availability")]
    public async Task<IActionResult> GetAvailability([FromQuery] Guid? propertyId)
    {
        var rules = await db.AvailabilityRules.Where(r => r.OwnerId == Uid && r.PropertyId == propertyId).ToListAsync();
        var ex = await db.AvailabilityExceptions.Where(r => r.OwnerId == Uid && r.PropertyId == propertyId).ToListAsync();
        var isDefault = rules.Count == 0;
        if (isDefault) rules = ViewingRules.DefaultRules(Uid, propertyId);   // what buyers are offered until the owner saves a schedule
        return Ok(new
        {
            data = new
            {
                ownerId = Uid, propertyId, isDefault,
                rules = rules.Select(r => new RuleDto(r.DayOfWeek, r.StartTime.ToString("HH:mm"), r.EndTime.ToString("HH:mm"), r.SlotDurationMin)),
                exceptions = ex.Select(e => new ExceptionDto(e.Date.ToString("yyyy-MM-dd"), e.Blocked ? "blocked" : "custom_window",
                    e.StartTime?.ToString("HH:mm"), e.EndTime?.ToString("HH:mm"), e.SlotDurationMin, e.Reason))
            }
        });
    }

    [Authorize(Policy = "Owner"), HttpPut("availability")]
    public async Task<IActionResult> SetAvailability([FromQuery] Guid? propertyId, AvailabilityDto dto)
    {
        var newRules = new List<AvailabilityRule>();
        foreach (var r in dto.Rules)
        {
            if (r.DayOfWeek is < 0 or > 6) throw ApiException.BadRequest("dayOfWeek must be 0-6.");
            if (!TimeOnly.TryParse(r.StartTime, out var s) || !TimeOnly.TryParse(r.EndTime, out var e) || e <= s)
                throw ApiException.BadRequest("Invalid time window.");
            if (r.SlotDurationMin is < 10 or > 240) throw ApiException.BadRequest("slotDurationMin must be 10-240.");
            newRules.Add(new AvailabilityRule { OwnerId = Uid, PropertyId = propertyId, DayOfWeek = r.DayOfWeek, StartTime = s, EndTime = e, SlotDurationMin = r.SlotDurationMin });
        }
        var newEx = new List<AvailabilityException>();
        foreach (var x in dto.Exceptions)
        {
            if (!DateOnly.TryParse(x.Date, out var d)) throw ApiException.BadRequest("Invalid exception date.");
            var blocked = x.Type == "blocked";
            TimeOnly? s = null, e = null;
            if (!blocked)
            {
                if (!TimeOnly.TryParse(x.StartTime, out var s1) || !TimeOnly.TryParse(x.EndTime, out var e1) || e1 <= s1)
                    throw ApiException.BadRequest("custom_window needs a valid start and end time.");
                s = s1; e = e1;
            }
            newEx.Add(new AvailabilityException { OwnerId = Uid, PropertyId = propertyId, Date = d, Blocked = blocked, StartTime = s, EndTime = e, SlotDurationMin = x.SlotDurationMin, Reason = x.Reason });
        }

        db.AvailabilityRules.RemoveRange(db.AvailabilityRules.Where(r => r.OwnerId == Uid && r.PropertyId == propertyId));
        db.AvailabilityExceptions.RemoveRange(db.AvailabilityExceptions.Where(r => r.OwnerId == Uid && r.PropertyId == propertyId));
        db.AvailabilityRules.AddRange(newRules);
        db.AvailabilityExceptions.AddRange(newEx);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- Slots (public to logged-in users) ----------
    [HttpGet("properties/{propertyId:guid}/slots")]
    public async Task<IActionResult> Slots(Guid propertyId, [FromQuery] string date)
    {
        if (!DateOnly.TryParse(date, out var d)) throw ApiException.BadRequest("date must be YYYY-MM-DD.");
        var p = await db.Properties.AsNoTracking().FirstOrDefaultAsync(x => x.Id == propertyId && x.Status == ListingStatus.Published)
                ?? throw ApiException.NotFound("Property not found.");
        var slots = await BuildSlots(p, d);
        return Ok(new
        {
            data = slots.Select(s => new
            {
                startsAt = s.StartsAtUtc, endsAt = s.EndsAtUtc, available = s.Available, unavailableReason = s.UnavailableReason
            })
        });
    }

    private async Task<List<Slot>> BuildSlots(Property p, DateOnly d)
    {
        // Property-specific rules override the owner's default (propertyId == null) rules.
        var rules = await db.AvailabilityRules.AsNoTracking().Where(r => r.OwnerId == p.OwnerId && r.PropertyId == p.Id).ToListAsync();
        var ex = await db.AvailabilityExceptions.AsNoTracking().Where(r => r.OwnerId == p.OwnerId && r.PropertyId == p.Id).ToListAsync();
        if (rules.Count == 0)
            rules = await db.AvailabilityRules.AsNoTracking().Where(r => r.OwnerId == p.OwnerId && r.PropertyId == null).ToListAsync();
        if (ex.Count == 0)
            ex = await db.AvailabilityExceptions.AsNoTracking().Where(r => r.OwnerId == p.OwnerId && r.PropertyId == null).ToListAsync();
        if (rules.Count == 0) rules = ViewingRules.DefaultRules(p.OwnerId);

        // Bookings are checked across the whole owner calendar so the owner can't be double-booked.
        var dayStart = ViewingRules.CairoToUtc(d, TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1).AddHours(1);
        var booked = await db.Viewings.AsNoTracking()
            .Where(v => v.OwnerId == p.OwnerId && v.StartsAt >= dayStart.AddHours(-1) && v.StartsAt < dayEnd
                        && (v.Status == ViewingStatus.Requested || v.Status == ViewingStatus.Confirmed || v.Status == ViewingStatus.RescheduleRequested))
            .Select(v => new { v.StartsAt, v.EndsAt }).ToListAsync();

        return ViewingRules.GenerateSlots(d, rules, ex, booked.Select(b => (b.StartsAt, b.EndsAt)), DateTime.UtcNow);
    }

    // ---------- Viewings ----------
    public record CreateViewingDto(Guid PropertyId, DateTime StartsAt, string? BuyerNotes, string? Lang);
    public record ConfirmDto(string? MeetingArea, string? MeetingPoint, string? OwnerNotes);
    public record ReasonDto(string? Reason);
    public record RescheduleDto(DateTime NewStartsAt, string? Reason);

    [HttpGet("viewings")]
    public async Task<IActionResult> Mine()
    {
        var list = await db.Viewings.AsNoTracking()
            .Where(v => v.BuyerId == Uid || v.OwnerId == Uid)
            .OrderByDescending(v => v.StartsAt).ToListAsync();
        return Ok(new { data = await ShapeMany(list), meta = new { total = list.Count } });
    }

    [HttpPost("viewings")]
    public async Task<IActionResult> Create(CreateViewingDto dto)
    {
        var p = await db.Properties.FirstOrDefaultAsync(x => x.Id == dto.PropertyId && x.Status == ListingStatus.Published)
                ?? throw ApiException.NotFound("Property not found.");
        if (p.OwnerId == Uid) throw ApiException.BadRequest("You cannot book a viewing of your own property.");
        if (p.Availability == Availability.SoldOrRented) throw ApiException.Conflict("This property is no longer available.");

        var slot = await FindAvailableSlot(p, dto.StartsAt);
        var notes = ContactFilter.Mask(dto.BuyerNotes, dto.Lang ?? "en");

        var v = new Viewing
        {
            PropertyId = p.Id, BuyerId = Uid, OwnerId = p.OwnerId,
            StartsAt = slot.StartsAtUtc, EndsAt = slot.EndsAtUtc,
            MeetingArea = p.Compound ?? p.City, BuyerNotes = notes
        };
        db.Viewings.Add(v);
        await SaveOrConflict();
        return Created($"/api/v1/viewings/{v.Id}", new { data = await One(v) });
    }

    [HttpPost("viewings/{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, ConfirmDto dto)
    {
        var v = await Load(id);
        RequireOwner(v);
        Move(v, ViewingStatus.Confirmed);
        v.MeetingArea = dto.MeetingArea ?? v.MeetingArea;
        var prop = await db.Properties.AsNoTracking().FirstAsync(x => x.Id == v.PropertyId);
        // The owner normally types the exact gate/unit. If they don't, fall back to the listing's compound or address.
        v.MeetingPoint = string.IsNullOrWhiteSpace(dto.MeetingPoint) ? (prop.Compound ?? prop.Location) : ContactFilter.Mask(dto.MeetingPoint.Trim());
        v.OwnerNotes = ContactFilter.Mask(dto.OwnerNotes);
        await db.SaveChangesAsync();
        return Ok(new { data = await One(v) });
    }

    [HttpPost("viewings/{id:guid}/decline")]
    public async Task<IActionResult> Decline(Guid id, ReasonDto dto)
    {
        var v = await Load(id);
        RequireOwner(v);
        if ((dto.Reason?.Trim().Length ?? 0) < 5) throw ApiException.BadRequest("A decline reason of at least 5 characters is required.");
        Move(v, ViewingStatus.Declined);
        v.DeclineReason = ContactFilter.Mask(dto.Reason);
        await db.SaveChangesAsync();
        return Ok(new { data = await One(v) });
    }

    [HttpPost("viewings/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, ReasonDto dto)
    {
        var v = await Load(id);
        if (v.BuyerId != Uid && v.OwnerId != Uid) throw ApiException.Forbidden("Not a participant.");
        Move(v, ViewingStatus.Cancelled);
        v.CancellationReason = ContactFilter.Mask(dto.Reason);
        await db.SaveChangesAsync();
        return Ok(new { data = await One(v) });
    }

    [HttpPost("viewings/{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id)
    {
        var v = await Load(id);
        RequireOwner(v);
        if (v.EndsAt > DateTime.UtcNow) throw ApiException.BadRequest("A viewing can only be completed after it ends.");
        Move(v, ViewingStatus.Completed);
        await db.SaveChangesAsync();
        return Ok(new { data = await One(v) });
    }

    [HttpPost("viewings/{id:guid}/reschedule")]
    public async Task<IActionResult> Reschedule(Guid id, RescheduleDto dto)
    {
        var v = await Load(id);
        if (v.BuyerId != Uid) throw ApiException.Forbidden("Only the buyer can request a reschedule.");
        var p = await db.Properties.AsNoTracking().FirstAsync(x => x.Id == v.PropertyId);
        var slot = await FindAvailableSlot(p, dto.NewStartsAt);
        Move(v, ViewingStatus.RescheduleRequested);
        v.RescheduleProposedStartsAt = slot.StartsAtUtc;
        v.RescheduleProposedEndsAt = slot.EndsAtUtc;
        v.RescheduleReason = ContactFilter.Mask(dto.Reason);
        await db.SaveChangesAsync();
        return Ok(new { data = await One(v) });
    }

    [HttpPost("viewings/{id:guid}/approve-reschedule")]
    public async Task<IActionResult> ApproveReschedule(Guid id)
    {
        var v = await Load(id);
        RequireOwner(v);
        if (v.Status != ViewingStatus.RescheduleRequested || v.RescheduleProposedStartsAt is null)
            throw ApiException.Conflict("No reschedule request is pending.");
        var p = await db.Properties.AsNoTracking().FirstAsync(x => x.Id == v.PropertyId);
        var slot = await FindAvailableSlot(p, v.RescheduleProposedStartsAt.Value);   // re-check: it may be taken now
        Move(v, ViewingStatus.Confirmed);
        v.StartsAt = slot.StartsAtUtc; v.EndsAt = slot.EndsAtUtc;
        ClearReschedule(v);
        await SaveOrConflict();
        return Ok(new { data = await One(v) });
    }

    [HttpPost("viewings/{id:guid}/decline-reschedule")]
    public async Task<IActionResult> DeclineReschedule(Guid id)
    {
        var v = await Load(id);
        RequireOwner(v);
        if (v.Status != ViewingStatus.RescheduleRequested) throw ApiException.Conflict("No reschedule request is pending.");
        Move(v, ViewingStatus.Confirmed);   // original time stands
        ClearReschedule(v);
        await db.SaveChangesAsync();
        return Ok(new { data = await One(v) });
    }

    // ---------- helpers ----------
    private async Task<Slot> FindAvailableSlot(Property p, DateTime startsAtUtc)
    {
        var utc = AsUtc(startsAtUtc);
        var cairoDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, ViewingRules.Cairo));
        var slots = await BuildSlots(p, cairoDate);
        var match = slots.FirstOrDefault(s => s.StartsAtUtc == utc)
                    ?? throw ApiException.BadRequest("That time is not an offered slot.");
        if (!match.Available)
            throw match.UnavailableReason == "booked"
                ? ApiException.Conflict("That slot is already booked.")
                : ApiException.BadRequest("That slot is in the past.");
        return match;
    }

    // SQL Server returns Kind=Unspecified and clients may omit the "Z"; ToUniversalTime() would
    // wrongly treat those as server-local time. Everything we store is UTC, so say so explicitly.
    private static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc)
    };

    private async Task<Viewing> Load(Guid id) =>
        await db.Viewings.FirstOrDefaultAsync(v => v.Id == id) ?? throw ApiException.NotFound("Viewing not found.");

    private void RequireOwner(Viewing v)
    {
        if (v.OwnerId != Uid) throw ApiException.Forbidden("Only the property owner can do this.");
    }

    private static void Move(Viewing v, ViewingStatus to)
    {
        if (!ViewingRules.CanTransition(v.Status, to))
            throw ApiException.Conflict($"Cannot change a {v.Status} viewing to {to}.");
        v.Status = to;
        v.UpdatedAt = DateTime.UtcNow;
    }

    private static void ClearReschedule(Viewing v)
    {
        v.RescheduleProposedStartsAt = null; v.RescheduleProposedEndsAt = null; v.RescheduleReason = null;
    }

    private async Task SaveOrConflict()
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { throw ApiException.Conflict("That slot was just booked by someone else."); }
    }

    private async Task<object> One(Viewing v) => (await ShapeMany([v]))[0];

    /// <summary>Viewings with the listing and people details the UI needs (title, address, cover photo, names).</summary>
    private async Task<List<object>> ShapeMany(IEnumerable<Viewing> viewings)
    {
        var list = viewings.ToList();
        var propIds = list.Select(v => v.PropertyId).Distinct().ToList();
        var props = await db.Properties.AsNoTracking().Include(p => p.Images).Where(p => propIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var names = await people.People(list.SelectMany(v => new[] { v.BuyerId, v.OwnerId }));

        return list.Select(v =>
        {
            props.TryGetValue(v.PropertyId, out var p);
            var party = v.BuyerId == Uid || v.OwnerId == Uid;
            var cover = p?.Images.OrderBy(i => i.SortOrder).FirstOrDefault(i => i.IsCover) ?? p?.Images.OrderBy(i => i.SortOrder).FirstOrDefault();
            return (object)new
            {
                v.Id, v.PropertyId, listingCode = p?.ListingCode ?? "", propertyTitle = p?.Title ?? "Listing no longer available",
                propertyTitleAr = p?.TitleAr, propertyLocation = p?.Location ?? "",
                propertyImage = cover is null ? null : signer.Url(cover.Id, high: true),
                v.BuyerId, buyerName = ChatShaper.PublicName(names.GetValueOrDefault(v.BuyerId)?.Name, true),
                v.OwnerId, ownerName = ChatShaper.PublicName(names.GetValueOrDefault(v.OwnerId)?.Name, false),
                ownerAvatar = names.GetValueOrDefault(v.OwnerId)?.Avatar,
                startsAt = DateTime.SpecifyKind(v.StartsAt, DateTimeKind.Utc), endsAt = DateTime.SpecifyKind(v.EndsAt, DateTimeKind.Utc),
                status = v.Status switch
                {
                    ViewingStatus.RescheduleRequested => "reschedule-requested",
                    _ => v.Status.ToString().ToLowerInvariant()
                },
                v.MeetingArea,
                // The exact meeting point is only revealed to participants once confirmed.
                meetingPoint = party && v.Status == ViewingStatus.Confirmed ? v.MeetingPoint : null,
                v.BuyerNotes, v.OwnerNotes, v.DeclineReason, v.CancellationReason, v.RescheduleReason,
                rescheduleProposedStartsAt = v.RescheduleProposedStartsAt is { } a ? DateTime.SpecifyKind(a, DateTimeKind.Utc) : (DateTime?)null,
                rescheduleProposedEndsAt = v.RescheduleProposedEndsAt is { } b ? DateTime.SpecifyKind(b, DateTimeKind.Utc) : (DateTime?)null,
                createdAt = DateTime.SpecifyKind(v.CreatedAt, DateTimeKind.Utc), updatedAt = DateTime.SpecifyKind(v.UpdatedAt, DateTimeKind.Utc)
            };
        }).ToList();
    }
}
