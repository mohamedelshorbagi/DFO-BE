using System.Security.Claims;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/v1/properties")]
public class PropertiesController(DfoDbContext db, PropertyShaper shaper) : ControllerBase
{
    public record PropertyQuery(
        Purpose? Purpose, Category? Category, string? City, string? Type,
        decimal? MinPrice, decimal? MaxPrice, string? Q, string? Sort, int? Cursor, int? Limit);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] PropertyQuery f)
    {
        var q = db.Properties.AsNoTracking().Include(p => p.Images)
            .Where(p => p.Status == ListingStatus.Published);

        if (f.Purpose is { } pu) q = q.Where(p => p.Purpose == pu);
        if (f.Category is { } ca) q = q.Where(p => p.Category == ca);
        if (!string.IsNullOrWhiteSpace(f.City)) q = q.Where(p => p.City == f.City);
        if (!string.IsNullOrWhiteSpace(f.Type)) q = q.Where(p => p.Type == f.Type);
        if (f.MinPrice is { } min) q = q.Where(p => p.Price >= min);
        if (f.MaxPrice is { } max) q = q.Where(p => p.Price <= max);
        if (!string.IsNullOrWhiteSpace(f.Q))
            q = q.Where(p => p.Title.Contains(f.Q) || p.Location.Contains(f.Q)
                          || (p.Compound != null && p.Compound.Contains(f.Q)) || p.ListingCode.Contains(f.Q));

        q = f.Sort switch
        {
            "price_asc" => q.OrderBy(p => p.Price),
            "price_desc" => q.OrderByDescending(p => p.Price),
            _ => q.OrderByDescending(p => p.CreatedAt)
        };

        var total = await q.CountAsync();
        var skip = f.Cursor ?? 0;
        var take = Math.Clamp(f.Limit ?? 20, 1, 50);
        var items = await q.Skip(skip).Take(take).ToListAsync();

        return Ok(new
        {
            data = await shaper.Many(items),
            meta = new { total, nextCursor = skip + take < total ? (int?)(skip + take) : null }
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var p = await db.Properties.Include(x => x.Images).FirstOrDefaultAsync(x => x.Id == id)
                ?? throw ApiException.NotFound("Property not found.");
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var privileged = User.IsInRole("admin") || User.IsInRole("moderator") || p.OwnerId == uid;
        if (p.Status != ListingStatus.Published && !privileged)
            throw ApiException.NotFound("Property not found.");
        return Ok(new { data = await shaper.One(p) });
    }

    /// <summary>The signed-in owner's own listings in every status (drafts, pending review, rejected...).</summary>
    [Authorize, HttpGet("/api/v1/me/properties")]
    public async Task<IActionResult> Mine()
    {
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var items = await db.Properties.AsNoTracking().Include(p => p.Images)
            .Where(p => p.OwnerId == uid).OrderByDescending(p => p.CreatedAt).ToListAsync();
        return Ok(new { data = await shaper.Many(items), meta = new { total = items.Count } });
    }

    public record AvailabilityDto(string Availability);

    /// <summary>Owner marks their listing available / under offer / sold or rented.</summary>
    [Authorize(Policy = "Owner"), HttpPatch("{id:guid}/availability")]
    public async Task<IActionResult> SetAvailability(Guid id, AvailabilityDto d)
    {
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var p = await db.Properties.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == uid)
                ?? throw ApiException.NotFound("Property not found.");   // 404 for other people's listings too
        p.Availability = d.Availability switch
        {
            "available" => Availability.Available,
            "under_offer" => Availability.UnderOffer,
            "sold_or_rented" => Availability.SoldOrRented,
            _ => throw ApiException.BadRequest("availability must be available, under_offer or sold_or_rented.")
        };
        await db.SaveChangesAsync();
        return NoContent();
    }

    public record CreatePropertyDto(
        string Title, string? TitleAr, Purpose Purpose, Category Category, string Type, decimal Price,
        decimal? DownPayment, int? InstallmentYears, string Location, string? LocationAr, string City,
        string? Compound, int Bedrooms, int Bathrooms, double Area, string? Finishing, string? Delivery,
        string Description, List<string>? Amenities = null);

    [Authorize(Policy = "Owner"), HttpPost]
    public async Task<IActionResult> Create(CreatePropertyDto d)
    {
        if (d.Price <= 0 || d.Area <= 0) throw ApiException.BadRequest("Price and area must be positive.");

        // Server owns the listing code (DH-YYYY-000123) — never the client.
        var seq = await db.NextValueAsync("ListingCodeSeq");
        var p = new Property
        {
            OwnerId = User.FindFirstValue(ClaimTypes.NameIdentifier)!,
            ListingCode = $"DH-{DateTime.UtcNow.Year}-{seq:D6}",
            Title = d.Title, TitleAr = d.TitleAr ?? d.Title, Purpose = d.Purpose, Category = d.Category,
            Type = d.Type, Price = d.Price, DownPayment = d.DownPayment, InstallmentYears = d.InstallmentYears,
            Location = d.Location, LocationAr = d.LocationAr, City = d.City, Compound = d.Compound,
            Bedrooms = d.Bedrooms, Bathrooms = d.Bathrooms, Area = d.Area, Finishing = d.Finishing,
            Delivery = d.Delivery, Description = d.Description,
            AmenitiesJson = System.Text.Json.JsonSerializer.Serialize(CleanAmenities(d.Amenities)),
            Status = ListingStatus.PendingVerification
        };
        db.Properties.Add(p);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = p.Id }, new { data = await shaper.One(p) });
    }

    // Free-text list from the client: cap the count and length so it can't be used to bloat rows.
    private static List<string> CleanAmenities(List<string>? raw) =>
        (raw ?? []).Select(a => (a ?? "").Trim()).Where(a => a.Length > 0).Select(a => a.Length > 80 ? a[..80] : a)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
}
