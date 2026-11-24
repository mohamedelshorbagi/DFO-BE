using System.Security.Claims;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Authorize]
[Route("api/v1/me/favorites")]
public class FavoritesController(DfoDbContext db, PropertyShaper shaper) : ControllerBase
{
    private const int MaxFavorites = 500;
    private string Uid => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IActionResult> List()
    {
        // Listings that were unpublished since saving are hidden, not deleted, so they reappear if re-published.
        var rows = await db.Favorites.AsNoTracking().Where(f => f.UserId == Uid)
            .Join(db.Properties.Include(p => p.Images).Where(p => p.Status == ListingStatus.Published),
                  f => f.PropertyId, p => p.Id, (f, p) => new { f.CreatedAt, Property = p })
            .OrderByDescending(x => x.CreatedAt).ToListAsync();
        return Ok(new
        {
            data = await shaper.Many(rows.Select(x => x.Property)),
            meta = new { total = rows.Count, ids = rows.Select(x => x.Property.Id) }
        });
    }

    [HttpPut("{propertyId:guid}")]
    public async Task<IActionResult> Add(Guid propertyId)
    {
        if (!await db.Properties.AnyAsync(p => p.Id == propertyId && p.Status == ListingStatus.Published))
            throw ApiException.NotFound("Property not found.");
        if (await db.Favorites.AnyAsync(f => f.UserId == Uid && f.PropertyId == propertyId))
            return NoContent();   // idempotent
        if (await db.Favorites.CountAsync(f => f.UserId == Uid) >= MaxFavorites)
            throw ApiException.Conflict($"You can save at most {MaxFavorites} listings.");

        db.Favorites.Add(new Favorite { UserId = Uid, PropertyId = propertyId });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return NoContent(); }   // lost a race with an identical request: already saved
        await db.Properties.Where(p => p.Id == propertyId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Likes, p => p.Likes + 1));   // atomic, no read-modify-write
        return NoContent();
    }

    [HttpDelete("{propertyId:guid}")]
    public async Task<IActionResult> Remove(Guid propertyId)
    {
        var removed = await db.Favorites.Where(f => f.UserId == Uid && f.PropertyId == propertyId).ExecuteDeleteAsync();
        if (removed > 0)
            await db.Properties.Where(p => p.Id == propertyId && p.Likes > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Likes, p => p.Likes - 1));
        return NoContent();
    }
}
