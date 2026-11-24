using System.Security.Claims;
using System.Text.RegularExpressions;
using Dfo.Application;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Authorize]
[Route("api/v1/alerts")]
public class AlertsController(DfoDbContext db, IAlertNotifier notifier) : ControllerBase
{
    private const int MaxAlertsPerUser = 20;
    private string Uid => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public record CriteriaDto(string? Purpose, string? Category, string? City, string? Type, string? Query, decimal? MinPrice, decimal? MaxPrice);
    public record CreateAlertDto(string Email, string? Phone, bool NotifyWhatsApp, AlertFrequency Frequency, CriteriaDto Criteria);
    public record UpdateAlertDto(AlertFrequency? Frequency, bool? IsActive);

    [HttpGet]
    public async Task<IActionResult> Mine()
    {
        var list = await db.SearchAlerts.AsNoTracking().Where(a => a.UserId == Uid).OrderByDescending(a => a.CreatedAt).ToListAsync();
        return Ok(new { data = list.Select(Shape), meta = new { total = list.Count } });
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateAlertDto d)
    {
        if (!Regex.IsMatch(d.Email ?? "", @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw ApiException.BadRequest("A valid email is required.");
        if (d.NotifyWhatsApp && !Regex.IsMatch(d.Phone ?? "", @"^(\+20|0020|0)?1[0125]\d{8}$"))
            throw ApiException.BadRequest("A valid Egyptian mobile number is required for WhatsApp alerts.");
        if (d.Criteria.MinPrice < 0 || d.Criteria.MaxPrice < 0 || d.Criteria.MinPrice > d.Criteria.MaxPrice)
            throw ApiException.BadRequest("Invalid price range.");
        if (await db.SearchAlerts.CountAsync(a => a.UserId == Uid) >= MaxAlertsPerUser)
            throw ApiException.Conflict($"You can have at most {MaxAlertsPerUser} alerts.");

        var c = d.Criteria;
        var a = new SearchAlert
        {
            UserId = Uid, Email = d.Email!.Trim(), Phone = d.Phone, NotifyWhatsApp = d.NotifyWhatsApp, Frequency = d.Frequency,
            Purpose = ParseEnum<Purpose>(c.Purpose), Category = ParseEnum<Category>(c.Category),
            City = Clean(c.City), Type = Clean(c.Type), Query = c.Query?.Trim(), MinPrice = c.MinPrice, MaxPrice = c.MaxPrice
        };
        db.SearchAlerts.Add(a);
        await db.SaveChangesAsync();
        return Created($"/api/v1/alerts/{a.Id}", new { data = Shape(a) });
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateAlertDto d)
    {
        var a = await Own(id);
        if (d.Frequency is { } f) a.Frequency = f;
        if (d.IsActive is { } on) a.IsActive = on;
        await db.SaveChangesAsync();
        return Ok(new { data = Shape(a) });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        db.SearchAlerts.Remove(await Own(id));
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>"Send test alert": previews what currently matches, without touching the alert's state.</summary>
    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        var a = await Own(id);
        var published = await db.Properties.AsNoTracking().Where(p => p.Status == ListingStatus.Published)
            .OrderByDescending(p => p.PublishedAt).Take(200).ToListAsync(ct);
        var hits = published.Where(p => AlertMatcher.Matches(a, p)).Take(5).ToList();
        var body = hits.Count == 0
            ? "This is a test alert. No listings currently match your criteria."
            : string.Join("\n", hits.Select(p => $"- {p.Title} ({p.City}) — EGP {p.Price:N0} [{p.ListingCode}]"));
        await notifier.SendAsync(new AlertMessage(a.Email, a.NotifyWhatsApp ? a.Phone : null, "Test alert", body), ct);
        return Ok(new { data = new { sent = true, matches = hits.Count } });
    }

    private async Task<SearchAlert> Own(Guid id) =>
        await db.SearchAlerts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == Uid)   // 404 (not 403) so ids can't be probed
        ?? throw ApiException.NotFound("Alert not found.");

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) || s.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : s.Trim();

    private static T? ParseEnum<T>(string? s) where T : struct, Enum =>
        Clean(s) is { } v && Enum.TryParse<T>(v, true, out var r) ? r : null;

    private static object Shape(SearchAlert a) => new
    {
        a.Id, a.Email, a.Phone, a.NotifyWhatsApp, frequency = a.Frequency.ToString().ToLowerInvariant(), a.IsActive,
        criteria = new
        {
            purpose = a.Purpose?.ToString().ToLowerInvariant() ?? "all", category = a.Category?.ToString().ToLowerInvariant(),
            a.City, a.Type, a.Query, a.MinPrice, a.MaxPrice
        },
        a.MatchCount, a.CreatedAt, a.LastNotifiedAt
    };
}
