using System.Security.Claims;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/v1")]
public class AdminController(DfoDbContext db, UserManager<AppUser> users, PropertyShaper shaper, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dp) : ControllerBase
{
    private string Uid => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    public record ReasonDto(string? Reason);
    public record RoleDto(string Role);
    public record VerifyDto(bool Verified);

    // ---------- Dashboard ----------
    [Authorize(Policy = "Admin"), HttpGet("admin/dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        var since = DateTime.UtcNow.AddDays(-7);
        return Ok(new
        {
            data = new
            {
                totalUsers = await db.Users.CountAsync(),
                activeListings = await db.Properties.CountAsync(p => p.Status == ListingStatus.Published),
                listingsAwaitingReview = await db.Properties.CountAsync(p => p.Status == ListingStatus.PendingVerification),
                rejectedListings = await db.Properties.CountAsync(p => p.Status == ListingStatus.Rejected),
                reportedListings = await db.Reports.CountAsync(r => r.TargetType == "listing" && (r.Status == ReportStatus.Open || r.Status == ReportStatus.Investigating)),
                recentSignupsCount = await db.Users.CountAsync(u => u.CreatedAt >= since)
            }
        });
    }

    // ---------- Users ----------
    [Authorize(Policy = "Admin"), HttpGet("admin/users")]
    public async Task<IActionResult> Users([FromQuery] string? q, [FromQuery] int? cursor, [FromQuery] int? limit)
    {
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(u => u.Name.Contains(q) || u.Email!.Contains(q));
        var total = await query.CountAsync();
        var skip = cursor ?? 0; var take = Math.Clamp(limit ?? 25, 1, 100);
        var page = await query.OrderByDescending(u => u.CreatedAt).Skip(skip).Take(take).ToListAsync();
        var ids = page.Select(u => u.Id).ToList();
        var counts = await db.Properties.AsNoTracking().Where(p => ids.Contains(p.OwnerId))
            .GroupBy(p => p.OwnerId).Select(g => new { g.Key, Submitted = g.Count(), Active = g.Count(p => p.Status == ListingStatus.Published) })
            .ToDictionaryAsync(x => x.Key);
        return Ok(new
        {
            data = page.Select(u => new
            {
                u.Id, u.Name, email = u.Email, phone = u.PhoneNumber, role = u.Role.ToString().ToLowerInvariant(),
                status = u.Suspended ? "suspended" : "active", u.Verified, u.SuspendedReason, u.SuspendedAt,
                submittedListingsCount = counts.GetValueOrDefault(u.Id)?.Submitted ?? 0,
                activeListingsCount = counts.GetValueOrDefault(u.Id)?.Active ?? 0,
                joinedDate = u.CreatedAt
            }),
            meta = new { total, nextCursor = skip + take < total ? (int?)(skip + take) : null }
        });
    }

    [Authorize(Policy = "Admin"), HttpPost("admin/users/{id}/suspend")]
    public async Task<IActionResult> Suspend(string id, ReasonDto dto)
    {
        if (id == Uid) throw ApiException.BadRequest("You cannot suspend yourself.");
        RequireText(dto.Reason, "A suspension reason of at least 5 characters is required.");
        var u = await Find(id);
        if (u.Role == UserRole.Admin && !User.IsInRole("admin")) throw ApiException.Forbidden("Only an admin can suspend an admin.");
        u.Suspended = true; u.SuspendedReason = dto.Reason!.Trim(); u.SuspendedAt = DateTime.UtcNow;
        await RefreshTokenRevoker.RevokeAllForUser(db, id);   // suspended users must not be able to refresh
        await users.UpdateSecurityStampAsync(u);
        Audit("user.suspend", "user", id, dto.Reason!);
        await db.SaveChangesAsync();
        return Ok(new { data = new { u.Id, status = "suspended" } });
    }

    [Authorize(Policy = "Admin"), HttpPost("admin/users/{id}/restore")]
    public async Task<IActionResult> Restore(string id)
    {
        var u = await Find(id);
        u.Suspended = false; u.SuspendedReason = null; u.SuspendedAt = null;
        Audit("user.restore", "user", id, "");
        await db.SaveChangesAsync();
        return Ok(new { data = new { u.Id, status = "active" } });
    }

    [Authorize(Policy = "Admin"), HttpPost("admin/users/{id}/verify")]
    public async Task<IActionResult> VerifyUser(string id, VerifyDto dto)
    {
        var u = await Find(id);
        u.Verified = dto.Verified;
        Audit(dto.Verified ? "user.verify" : "user.unverify", "user", id, "");
        await db.SaveChangesAsync();
        return Ok(new { data = new { u.Id, u.Verified } });
    }

    // Role changes (including creating admins) are admin-only; moderators cannot escalate anyone.
    [Authorize(Roles = "admin"), HttpPut("admin/users/{id}/role")]
    public async Task<IActionResult> SetRole(string id, RoleDto dto)
    {
        if (!Enum.TryParse<UserRole>(dto.Role, true, out var role)) throw ApiException.BadRequest("Unknown role.");
        var u = await Find(id);
        if (u.Role == UserRole.Admin && role != UserRole.Admin &&
            await db.Users.CountAsync(x => x.Role == UserRole.Admin && !x.Suspended) <= 1)
            throw ApiException.Conflict("Cannot demote the last admin.");
        var old = u.Role;
        u.Role = role;
        await users.UpdateSecurityStampAsync(u);
        Audit("user.role", "user", id, $"{old} -> {role}");
        await db.SaveChangesAsync();
        return Ok(new { data = new { u.Id, role = role.ToString().ToLowerInvariant() } });
    }

    // ---------- Listing moderation ----------
    [Authorize(Policy = "Admin"), HttpGet("admin/listings")]
    public async Task<IActionResult> Listings([FromQuery] ListingStatus? status, [FromQuery] bool? flagged)
    {
        var q = db.Properties.AsNoTracking().Include(p => p.Images).AsQueryable();
        if (status is { } s) q = q.Where(p => p.Status == s);
        if (flagged is { } f) q = q.Where(p => p.Flagged == f);
        var list = await q.OrderByDescending(p => p.CreatedAt).Take(500).ToListAsync();
        var shaped = await shaper.Many(list);

        var ids = list.Select(p => p.Id).ToList();
        var verifications = await db.LegalVerifications.AsNoTracking().Include(v => v.Documents).Where(v => ids.Contains(v.PropertyId)).ToListAsync();
        var latest = verifications.GroupBy(v => v.PropertyId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.SubmittedAt).First());

        return Ok(new
        {
            data = list.Select((p, n) => new
            {
                property = shaped[n], flagged = p.Flagged,
                legalVerification = latest.TryGetValue(p.Id, out var v) ? LegalDto(v) : null
            }),
            meta = new { total = list.Count }
        });
    }

    private static string Snake(string pascal) => Regex.Replace(pascal, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    private static string SizeText(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    /// <summary>What a reviewer sees. The national ID is masked here: staff verify it matches the documents, they don't need all 14 digits in a list.</summary>
    private object LegalDto(LegalVerification v) => new
    {
        id = v.Id, referenceNumber = v.ReferenceNumber, docType = Snake(v.DocType.ToString()),
        deedOrContractNumber = v.DeedOrContractNumber, developerOrNotaryOffice = v.DeveloperOrNotaryOffice,
        nationalIdNumber = MaskId(v.NationalIdEncrypted), legalDeclarationAccepted = v.LegalDeclarationAccepted,
        reviewStatus = Snake(v.ReviewStatus.ToString()), rejectionReason = v.RejectionReason, moreInfoQuery = v.MoreInfoQuery,
        submittedAt = DateTime.SpecifyKind(v.SubmittedAt, DateTimeKind.Utc), estimatedReviewHours = 24,
        documents = v.Documents.OrderBy(d => d.UploadedAt).Select(d => new
        {
            id = d.Id, name = d.FileName, category = d.Category, fileType = d.ContentType == "application/pdf" ? "pdf" : "image",
            fileSize = SizeText(d.SizeBytes), uploadedAt = DateTime.SpecifyKind(d.UploadedAt, DateTimeKind.Utc)
        })
    };

    private string MaskId(string encrypted)
    {
        try { var id = dp.CreateProtector("Dfo.NationalId.v1").Unprotect(encrypted); return new string('*', Math.Max(0, id.Length - 4)) + id[^4..]; }
        catch { return "**********"; }
    }

    [Authorize(Policy = "Admin"), HttpGet("admin/properties/{id:guid}/review-history")]
    public async Task<IActionResult> ReviewHistoryFor(Guid id)
    {
        var rows = await db.ReviewHistory.AsNoTracking().Where(r => r.PropertyId == id).OrderByDescending(r => r.Timestamp).ToListAsync();
        var names = await db.Users.AsNoTracking().Where(u => rows.Select(r => r.AdminId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name);
        return Ok(new
        {
            data = rows.Select(r => new
            {
                r.Id, propertyId = r.PropertyId, adminId = r.AdminId, adminName = names.GetValueOrDefault(r.AdminId) ?? "Administrator",
                action = r.Action, reason = r.Reason, notes = r.Reason, timestamp = DateTime.SpecifyKind(r.Timestamp, DateTimeKind.Utc)
            })
        });
    }

    [Authorize(Policy = "Admin"), HttpPost("admin/listings/{id:guid}/{verb}")]
    public async Task<IActionResult> ModerateListing(Guid id, string verb, ReasonDto dto)
    {
        var p = await db.Properties.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Listing not found.");
        switch (verb)
        {
            case "pause":
                RequireText(dto.Reason, "A reason of at least 5 characters is required.");
                if (p.Status != ListingStatus.Published) throw ApiException.Conflict("Only published listings can be paused.");
                p.Status = ListingStatus.Paused; break;
            case "unpause":
                if (p.Status != ListingStatus.Paused) throw ApiException.Conflict("Listing is not paused.");
                p.Status = ListingStatus.Published; break;
            case "archive":
                RequireText(dto.Reason, "A reason of at least 5 characters is required.");
                p.Status = ListingStatus.Archived; break;
            case "flag": p.Flagged = true; break;
            case "unflag": p.Flagged = false; break;
            default: throw ApiException.NotFound("Unknown action.");
        }
        db.ReviewHistory.Add(new ReviewHistory { PropertyId = p.Id, AdminId = Uid, Action = verb, Reason = dto.Reason });
        Audit($"listing.{verb}", "listing", id.ToString(), dto.Reason ?? "");
        await db.SaveChangesAsync();
        return Ok(new { data = new { p.Id, status = p.Status.ToString(), p.Flagged } });
    }

    // ---------- Reports ----------
    public record CreateReportDto(string TargetType, string TargetId, string Reason, string? Description);
    private static readonly string[] TargetTypes = ["listing", "user", "chat_circumvention"];

    [Authorize, HttpPost("reports")]
    public async Task<IActionResult> CreateReport(CreateReportDto d)
    {
        if (!TargetTypes.Contains(d.TargetType)) throw ApiException.BadRequest("Unknown target type.");
        if (string.IsNullOrWhiteSpace(d.TargetId) || string.IsNullOrWhiteSpace(d.Reason)) throw ApiException.BadRequest("Target and reason are required.");
        if (d.Reason.Length > 200 || (d.Description?.Length ?? 0) > 2000) throw ApiException.BadRequest("Text too long.");
        // Stop one user flooding the queue with duplicates against the same target.
        if (await db.Reports.AnyAsync(r => r.ReporterId == Uid && r.TargetType == d.TargetType && r.TargetId == d.TargetId
                && (r.Status == ReportStatus.Open || r.Status == ReportStatus.Investigating)))
            throw ApiException.Conflict("You already have an open report for this item.");
        var r = new Report { TargetType = d.TargetType, TargetId = d.TargetId, ReporterId = Uid, Reason = d.Reason.Trim(), Description = d.Description?.Trim() ?? "" };
        db.Reports.Add(r);
        await db.SaveChangesAsync();
        return Created($"/api/v1/admin/reports/{r.Id}", new { data = new { r.Id, status = "open" } });
    }

    [Authorize(Policy = "Admin"), HttpGet("admin/reports")]
    public async Task<IActionResult> Reports([FromQuery] ReportStatus? status)
    {
        var q = db.Reports.AsNoTracking().AsQueryable();
        if (status is { } s) q = q.Where(r => r.Status == s);
        var list = await q.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync();
        var reporters = await db.Users.AsNoTracking().Where(u => list.Select(r => r.ReporterId).Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        var listingIds = list.Where(r => r.TargetType == "listing" && Guid.TryParse(r.TargetId, out _)).Select(r => Guid.Parse(r.TargetId)).ToList();
        var titles = await db.Properties.AsNoTracking().Where(p => listingIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id.ToString(), p => p.Title);
        var userTargets = list.Where(r => r.TargetType == "user").Select(r => r.TargetId).ToList();
        var userNames = await db.Users.AsNoTracking().Where(u => userTargets.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name);
        return Ok(new
        {
            data = list.Select(r => new
            {
                r.Id, r.TargetType, r.TargetId,
                targetTitle = r.TargetType == "listing" ? titles.GetValueOrDefault(r.TargetId) ?? r.TargetId
                            : r.TargetType == "user" ? userNames.GetValueOrDefault(r.TargetId) ?? r.TargetId : r.TargetId,
                r.ReporterId, reporterName = reporters.GetValueOrDefault(r.ReporterId)?.Name ?? "Unknown",
                reporterEmail = reporters.GetValueOrDefault(r.ReporterId)?.Email ?? "", r.Reason, r.Description, status = r.Status.ToString().ToLowerInvariant(),
                r.AdminNotes, r.ActionTaken, r.ResolvedBy, r.CreatedAt, r.UpdatedAt, r.ResolvedAt
            }),
            meta = new { total = list.Count }
        });
    }

    public record ResolveReportDto(string? AdminNotes, string? ActionTaken);

    [Authorize(Policy = "Admin"), HttpPost("admin/reports/{id:guid}/{verb}")]
    public async Task<IActionResult> ResolveReport(Guid id, string verb, ResolveReportDto dto)
    {
        var r = await db.Reports.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Report not found.");
        var next = verb switch
        {
            "investigate" => ReportStatus.Investigating, "resolve" => ReportStatus.Resolved,
            "dismiss" => ReportStatus.Dismissed, "escalate" => ReportStatus.Escalated,
            _ => throw ApiException.NotFound("Unknown action.")
        };
        if (r.Status is ReportStatus.Resolved or ReportStatus.Dismissed) throw ApiException.Conflict("This report is already closed.");
        if (next is ReportStatus.Resolved or ReportStatus.Dismissed) RequireText(dto.AdminNotes, "Admin notes of at least 5 characters are required to close a report.");
        r.Status = next; r.AdminNotes = dto.AdminNotes ?? r.AdminNotes; r.ActionTaken = dto.ActionTaken ?? r.ActionTaken; r.UpdatedAt = DateTime.UtcNow;
        if (next is ReportStatus.Resolved or ReportStatus.Dismissed) { r.ResolvedBy = Uid; r.ResolvedAt = DateTime.UtcNow; }
        Audit($"report.{verb}", "report", id.ToString(), dto.AdminNotes ?? "");
        await db.SaveChangesAsync();
        return Ok(new { data = new { r.Id, status = r.Status.ToString().ToLowerInvariant() } });
    }

    // ---------- Settings ----------
    [Authorize(Policy = "Admin"), HttpGet("admin/settings")]
    public async Task<IActionResult> GetSettings() => Ok(new { data = await LoadSettings() });

    [Authorize(Roles = "admin"), HttpPut("admin/settings")]
    public async Task<IActionResult> PutSettings(MarketplaceSettings d)
    {
        if (d.MaxImagesPerListing is < 1 or > 50) throw ApiException.BadRequest("maxImagesPerListing must be 1-50.");
        if (d.MarketplaceCommissionPercent is < 0 or > 100) throw ApiException.BadRequest("Commission must be 0-100.");
        var s = await LoadSettings();
        d.Id = s.Id;
        db.Entry(s).CurrentValues.SetValues(d);
        Audit("settings.update", "settings", "1", "");
        await db.SaveChangesAsync();
        return Ok(new { data = s });
    }

    private async Task<MarketplaceSettings> LoadSettings()
    {
        var s = await db.Settings.FirstOrDefaultAsync();
        if (s is null) { s = new MarketplaceSettings(); db.Settings.Add(s); await db.SaveChangesAsync(); }
        return s;
    }

    // ---------- Taxonomies ----------
    private static readonly string[] Kinds = ["propertyType", "finishing", "amenity", "city", "compound"];
    public record TaxonomyDto(string Kind, string Key, string En, string? Ar, string? Category, string? Icon, string? ParentKey);

    [AllowAnonymous, HttpGet("taxonomies")]
    public async Task<IActionResult> Taxonomies()
    {
        var all = await db.Taxonomies.AsNoTracking().OrderBy(t => t.En).ToListAsync();
        return Ok(new
        {
            data = new
            {
                propertyTypes = all.Where(t => t.Kind == "propertyType").Select(t => new { id = t.Key, category = t.Category, en = t.En, ar = t.Ar, icon = t.Icon }),
                finishingTypes = all.Where(t => t.Kind == "finishing").Select(t => new { id = t.Key, en = t.En, ar = t.Ar }),
                amenities = all.Where(t => t.Kind == "amenity").Select(t => new { id = t.Key, en = t.En, ar = t.Ar, category = t.Category }),
                supportedLocations = all.Where(t => t.Kind == "city").Select(c => new
                {
                    cityKey = c.Key, city = c.En, cityAr = c.Ar,
                    compounds = all.Where(t => t.Kind == "compound" && t.ParentKey == c.Key).Select(t => t.En)
                })
            }
        });
    }

    [Authorize(Policy = "Admin"), HttpPost("admin/taxonomies")]
    public async Task<IActionResult> AddTaxonomy(TaxonomyDto d)
    {
        if (!Kinds.Contains(d.Kind)) throw ApiException.BadRequest("Unknown taxonomy kind.");
        if (string.IsNullOrWhiteSpace(d.Key) || string.IsNullOrWhiteSpace(d.En)) throw ApiException.BadRequest("Key and English name are required.");
        if (d.Kind == "compound" && !await db.Taxonomies.AnyAsync(t => t.Kind == "city" && t.Key == d.ParentKey))
            throw ApiException.BadRequest("A compound needs an existing parent city key.");
        if (await db.Taxonomies.AnyAsync(t => t.Kind == d.Kind && t.Key == d.Key)) throw ApiException.Conflict("That key already exists.");
        var t = new TaxonomyItem { Kind = d.Kind, Key = d.Key.Trim(), En = d.En.Trim(), Ar = d.Ar?.Trim() ?? d.En.Trim(), Category = d.Category, Icon = d.Icon, ParentKey = d.ParentKey };
        db.Taxonomies.Add(t);
        Audit("taxonomy.add", "taxonomy", $"{t.Kind}:{t.Key}", t.En);
        await db.SaveChangesAsync();
        return Created($"/api/v1/taxonomies", new { data = new { t.Id, t.Kind, t.Key } });
    }

    [Authorize(Policy = "Admin"), HttpDelete("admin/taxonomies/{id:guid}")]
    public async Task<IActionResult> DeleteTaxonomy(Guid id)
    {
        var t = await db.Taxonomies.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Not found.");
        if (t.Kind == "city" && await db.Taxonomies.AnyAsync(x => x.Kind == "compound" && x.ParentKey == t.Key))
            throw ApiException.Conflict("Remove this city's compounds first.");
        db.Taxonomies.Remove(t);
        Audit("taxonomy.delete", "taxonomy", $"{t.Kind}:{t.Key}", t.En);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- Audit log ----------
    [Authorize(Policy = "Admin"), HttpGet("admin/audit-logs")]
    public async Task<IActionResult> AuditLogs([FromQuery] string? action, [FromQuery] int? cursor, [FromQuery] int? limit)
    {
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
        var total = await q.CountAsync();
        var skip = cursor ?? 0; var take = Math.Clamp(limit ?? 50, 1, 200);
        var page = await q.OrderByDescending(a => a.Timestamp).Skip(skip).Take(take).ToListAsync();
        var names = await db.Users.AsNoTracking().Where(u => page.Select(a => a.ActorId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.Name, u.Email });
        return Ok(new
        {
            data = page.Select(a => new
            {
                a.Id, a.ActorId, actorName = names.GetValueOrDefault(a.ActorId)?.Name, actorEmail = names.GetValueOrDefault(a.ActorId)?.Email,
                a.Action, a.TargetType, a.TargetId, details = a.Details, a.Timestamp, a.IpAddress
            }),
            meta = new { total, nextCursor = skip + take < total ? (int?)(skip + take) : null }
        });
    }

    // ---------- helpers ----------
    private async Task<AppUser> Find(string id) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == id) ?? throw ApiException.NotFound("User not found.");

    private static void RequireText(string? s, string message)
    {
        if ((s?.Trim().Length ?? 0) < 5) throw ApiException.BadRequest(message);
    }

    private void Audit(string action, string targetType, string targetId, string details) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = Uid, Action = action, TargetType = targetType, TargetId = targetId, Details = details,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
}
