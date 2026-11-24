using System.Security.Claims;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

[ApiController]
[Route("api/v1")]
public class ImagesController(DfoDbContext db, MediaUrlSigner signer, PropertyShaper shaper, IConfiguration cfg) : ControllerBase
{
    private const long MaxFileBytes = 8 * 1024 * 1024;
    private const int MaxFilesPerRequest = 10;
    private const long MaxPixels = 40_000_000;      // decompression-bomb guard: a small file can decode to a huge bitmap
    private const int HiEdge = 1600, LoEdge = 480;

    private string Uid => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private string Root => Path.GetFullPath(cfg["Media:Path"] ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "media"));

    // ---------- Upload ----------
    [Authorize(Policy = "Owner"), HttpPost("properties/{propertyId:guid}/images")]
    [RequestSizeLimit(MaxFilesPerRequest * MaxFileBytes + 1024 * 64)]
    public async Task<IActionResult> Upload(Guid propertyId, [FromForm] List<IFormFile> files, CancellationToken ct)
    {
        var p = await OwnProperty(propertyId, includeImages: true);
        if (p.Status == ListingStatus.Archived) throw ApiException.Conflict("Archived listings cannot be edited.");
        if (files is null || files.Count == 0) throw ApiException.BadRequest("No files uploaded.");
        if (files.Count > MaxFilesPerRequest) throw ApiException.BadRequest($"At most {MaxFilesPerRequest} files per request.");

        var max = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.MaxImagesPerListing ?? 15;
        if (p.Images.Count + files.Count > max) throw ApiException.BadRequest($"A listing can have at most {max} images.");

        Directory.CreateDirectory(Root);
        var written = new List<string>();
        var added = new List<PropertyImage>();
        try
        {
            var order = p.Images.Count == 0 ? 0 : p.Images.Max(i => i.SortOrder) + 1;
            foreach (var f in files)
            {
                if (f.Length == 0) throw ApiException.BadRequest($"'{f.FileName}' is empty.");
                if (f.Length > MaxFileBytes) throw ApiException.BadRequest($"'{f.FileName}' exceeds 8 MB.");

                var key = Guid.NewGuid().ToString("N");
                var (w, h) = await Process(f, key, written, ct);
                var img = new PropertyImage
                {
                    PropertyId = p.Id, StorageKey = key, Width = w, Height = h,
                    SortOrder = order++, IsCover = p.Images.Count + added.Count == 0
                };
                added.Add(img);
            }
            db.PropertyImages.AddRange(added);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            foreach (var path in written) { try { System.IO.File.Delete(path); } catch { /* best effort cleanup */ } }
            throw;
        }

        // No p.Images.AddRange here: after SaveChanges, EF's relationship fix-up has already added them.
        return Created($"/api/v1/properties/{p.Id}", new { data = await shaper.One(p) });
    }

    /// <summary>Decode, validate, strip metadata, re-encode. The client's bytes are never stored or served as-is.</summary>
    private async Task<(int w, int h)> Process(IFormFile f, string key, List<string> written, CancellationToken ct)
    {
        await using var s = f.OpenReadStream();
        ImageInfo info;
        try { info = await Image.IdentifyAsync(s, ct); }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        { throw ApiException.BadRequest($"'{f.FileName}' is not a valid image."); }

        var fmt = info.Metadata.DecodedImageFormat;
        if (fmt is not (JpegFormat or PngFormat or WebpFormat))
            throw ApiException.BadRequest("Only JPEG, PNG or WebP images are allowed.");
        if ((long)info.Width * info.Height > MaxPixels) throw ApiException.BadRequest("Image dimensions are too large.");
        if (info.Width < 400 || info.Height < 300) throw ApiException.BadRequest("Image must be at least 400x300 pixels.");

        s.Position = 0;
        Image img;
        try { img = await Image.LoadAsync(s, ct); }
        catch (Exception ex) when (ex is InvalidImageContentException or NotSupportedException)
        { throw ApiException.BadRequest($"'{f.FileName}' is corrupt."); }

        using (img)
        {
            img.Mutate(c => c.AutoOrient());
            // Re-encoding drops the original bytes, but metadata (EXIF GPS, camera serials) is carried over unless cleared.
            img.Metadata.ExifProfile = null; img.Metadata.IccProfile = null; img.Metadata.XmpProfile = null; img.Metadata.IptcProfile = null;

            var (w, h) = (img.Width, img.Height);
            await Save(img, HiEdge, 85, Path.Combine(Root, $"{key}.hi.jpg"), written, ct);
            await Save(img, LoEdge, 60, Path.Combine(Root, $"{key}.lo.jpg"), written, ct);
            return (w, h);
        }
    }

    private static async Task Save(Image src, int edge, int quality, string path, List<string> written, CancellationToken ct)
    {
        using var copy = src.Clone(c => c.Resize(new ResizeOptions
        {
            Size = new Size(edge, edge), Mode = ResizeMode.Max   // keeps aspect ratio, never upscales past the edge cap
        }));
        written.Add(path);
        await copy.SaveAsJpegAsync(path, new JpegEncoder { Quality = quality }, ct);
    }

    // ---------- Manage ----------
    public record OrderDto(List<Guid> ImageIds);

    [Authorize(Policy = "Owner"), HttpPut("properties/{propertyId:guid}/images/order")]
    public async Task<IActionResult> Reorder(Guid propertyId, OrderDto dto)
    {
        var p = await OwnProperty(propertyId, true);
        if (dto.ImageIds.Count != p.Images.Count || dto.ImageIds.Distinct().Count() != dto.ImageIds.Count ||
            !dto.ImageIds.All(id => p.Images.Any(i => i.Id == id)))
            throw ApiException.BadRequest("imageIds must list every image of this listing exactly once.");
        for (var n = 0; n < dto.ImageIds.Count; n++) p.Images.First(i => i.Id == dto.ImageIds[n]).SortOrder = n;
        await db.SaveChangesAsync();
        return Ok(new { data = await shaper.One(p) });
    }

    [Authorize(Policy = "Owner"), HttpPut("properties/{propertyId:guid}/images/{imageId:guid}/cover")]
    public async Task<IActionResult> SetCover(Guid propertyId, Guid imageId)
    {
        var p = await OwnProperty(propertyId, true);
        if (p.Images.All(i => i.Id != imageId)) throw ApiException.NotFound("Image not found.");
        foreach (var i in p.Images) i.IsCover = i.Id == imageId;
        await db.SaveChangesAsync();
        return Ok(new { data = await shaper.One(p) });
    }

    [Authorize(Policy = "Owner"), HttpDelete("properties/{propertyId:guid}/images/{imageId:guid}")]
    public async Task<IActionResult> Delete(Guid propertyId, Guid imageId)
    {
        var p = await OwnProperty(propertyId, true);
        var img = p.Images.FirstOrDefault(i => i.Id == imageId) ?? throw ApiException.NotFound("Image not found.");
        db.PropertyImages.Remove(img);
        p.Images.Remove(img);
        if (img.IsCover && p.Images.Count > 0) p.Images.OrderBy(i => i.SortOrder).First().IsCover = true;
        await db.SaveChangesAsync();
        foreach (var suffix in new[] { "hi", "lo" })
        {
            try { System.IO.File.Delete(Path.Combine(Root, $"{img.StorageKey}.{suffix}.jpg")); } catch { /* orphan file is harmless */ }
        }
        return Ok(new { data = await shaper.One(p) });
    }

    // ---------- Serve (signed, expiring) ----------
    [AllowAnonymous, HttpGet("media/{id:guid}")]
    public async Task<IActionResult> Serve(Guid id, [FromQuery] string q, [FromQuery] long exp, [FromQuery] string sig)
    {
        // Same 404 for bad signature and missing image, so ids can't be probed.
        if (!signer.Valid(id, q, exp, sig)) throw ApiException.NotFound("Not found.");
        var key = await db.PropertyImages.AsNoTracking().Where(i => i.Id == id).Select(i => i.StorageKey).FirstOrDefaultAsync()
                  ?? throw ApiException.NotFound("Not found.");
        var path = Path.GetFullPath(Path.Combine(Root, $"{key}.{(q == "high" ? "hi" : "lo")}.jpg"));
        if (!path.StartsWith(Root) || !System.IO.File.Exists(path)) throw ApiException.NotFound("Not found.");
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "private, max-age=300";
        return PhysicalFile(path, "image/jpeg");
    }

    private async Task<Property> OwnProperty(Guid id, bool includeImages)
    {
        var q = db.Properties.AsQueryable();
        if (includeImages) q = q.Include(p => p.Images);
        return await q.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == Uid)
               ?? throw ApiException.NotFound("Property not found.");   // 404 for someone else's listing, too
    }
}
