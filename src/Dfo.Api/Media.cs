using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.Json;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.EntityFrameworkCore;

/// <summary>Signs and verifies expiring media URLs (HMAC-SHA256 over id|quality|expiry).</summary>
public class MediaUrlSigner(IConfiguration cfg)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        cfg["Media:SigningKey"] ?? "dev-only-media-key-change-me-0123456789abcdef");
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    public string Url(Guid imageId, bool high)
    {
        var q = high ? "high" : "low";
        var exp = DateTimeOffset.UtcNow.Add(Ttl).ToUnixTimeSeconds();
        return $"/api/v1/media/{imageId}?q={q}&exp={exp}&sig={Sign(imageId, q, exp)}";
    }

    /// <summary>A 10-minute link for staff to open an ownership document in a new tab (a browser tab can't send auth headers).</summary>
    public string DocUrl(Guid docId)
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        return $"/api/v1/verification-documents/{docId}/file?exp={exp}&sig={Sign(docId, "doc", exp)}";
    }

    public bool ValidDoc(Guid docId, long exp, string? sig)
    {
        if (exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(docId, "doc", exp)), Encoding.ASCII.GetBytes(sig ?? ""));
    }

    public bool Valid(Guid imageId, string q, long exp, string sig)
    {
        if (q is not ("low" or "high") || exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(imageId, q, exp));
        var given = Encoding.ASCII.GetBytes(sig ?? "");
        return CryptographicOperations.FixedTimeEquals(expected, given);   // constant-time compare
    }

    private string Sign(Guid id, string q, long exp)
    {
        using var h = new HMACSHA256(_key);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{id:N}|{q}|{exp}"))).ToLowerInvariant();
    }
}

/// <summary>Shapes properties for the API. High-res images only for signed-in users; visitors get low-res.</summary>
public class PropertyShaper(MediaUrlSigner signer, IHttpContextAccessor http, DfoDbContext db)
{
    private bool High => http.HttpContext?.User.Identity?.IsAuthenticated == true;

    public async Task<object> One(Property p) => (await Many([p]))[0];

    /// <summary>Shapes a page of listings with a single owner lookup (no per-listing query).</summary>
    public async Task<List<object>> Many(IEnumerable<Property> props)
    {
        var list = props.ToList();
        var ids = list.Select(p => p.OwnerId).Distinct().ToList();
        var owners = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Name, u.Verified, u.Avatar, u.CreatedAt })
            .ToDictionaryAsync(u => u.Id);

        return list.Select(p =>
        {
            var o = owners.GetValueOrDefault(p.OwnerId);
            var imgs = p.Images.OrderBy(i => i.SortOrder).ToList();
            var cover = imgs.FirstOrDefault(i => i.IsCover) ?? imgs.FirstOrDefault();
            var published = p.Status == ListingStatus.Published;
            return (object)new
            {
                p.Id, p.ListingCode, p.OwnerId, p.Title, p.TitleAr,
                // Public owner card: name/verified/member-since only. Never email, phone or national ID.
                owner = new
                {
                    id = p.OwnerId, name = o?.Name ?? "Owner", avatar = o?.Avatar, verified = o?.Verified ?? false,
                    memberSince = (o?.CreatedAt ?? p.CreatedAt).ToString("MMMM yyyy", CultureInfo.InvariantCulture)
                },
                verifiedOwner = o?.Verified ?? false,
                verifiedListing = published,   // only listings that passed legal review are ever Published
                sellerType = "Owner",
                purpose = p.Purpose.ToString().ToLowerInvariant(),
                category = p.Category.ToString().ToLowerInvariant(),
                p.Type, p.Price, p.DownPayment, p.InstallmentYears,
                monthlyInstallment = Monthly(p), p.Location, p.LocationAr, p.City, p.Compound,
                p.Lat, p.Lng, p.Bedrooms, p.Bathrooms, p.Area, p.Finishing, p.Delivery, p.Description, p.Featured,
                amenities = Amenities(p),
                status = p.Status == ListingStatus.PendingVerification ? "Pending Verification" : p.Status.ToString(),
                availability = p.Availability switch
                {
                    Availability.UnderOffer => "under_offer",
                    Availability.SoldOrRented => "sold_or_rented",
                    _ => "available"
                },
                p.Likes, p.ViewsCount, p.LeadsCount, createdDate = p.CreatedAt,
                image = cover is null ? null : signer.Url(cover.Id, High),
                gallery = imgs.Select(i => signer.Url(i.Id, High)),
                images = imgs.Select(i => new { i.Id, i.IsCover, i.SortOrder, i.Width, i.Height })
            };
        }).ToList();
    }

    public static decimal? Monthly(Property p) =>
        p.DownPayment is { } d && p.InstallmentYears is > 0 and var y && p.Price > d
            ? Math.Round((p.Price - d) / (y * 12), 0) : null;

    public static string[] Amenities(Property p)
    {
        try { return JsonSerializer.Deserialize<string[]>(p.AmenitiesJson) ?? []; }
        catch (JsonException) { return []; }   // a corrupt value must never take the listing page down
    }
}
