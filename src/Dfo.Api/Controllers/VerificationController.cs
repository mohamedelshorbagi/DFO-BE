using System.Security.Claims;
using System.Text.RegularExpressions;
using Dfo.Application;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Authorize]
[Route("api/v1")]
public class VerificationController(DfoDbContext db, IDataProtectionProvider dp, IConfiguration cfg, MediaUrlSigner signer) : ControllerBase
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private static readonly Dictionary<string, string[]> AllowedTypes = new()
    {
        ["application/pdf"] = [".pdf"],
        ["image/jpeg"] = [".jpg", ".jpeg"],
        ["image/png"] = [".png"]
    };
    private static readonly string[] DocCategories = ["deed", "national_id", "maintenance_clearance", "power_of_attorney"];

    private string Uid => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private IDataProtector Protector => dp.CreateProtector("Dfo.NationalId.v1");

    // ---------- Owner ----------
    public record SubmitDto(DocType DocType, string DeedOrContractNumber, string? DeveloperOrNotaryOffice,
                            string NationalIdNumber, bool LegalDeclarationAccepted);

    [Authorize(Policy = "Owner"), HttpPost("properties/{propertyId:guid}/verification")]
    public async Task<IActionResult> Submit(Guid propertyId, SubmitDto d)
    {
        var p = await db.Properties.FirstOrDefaultAsync(x => x.Id == propertyId && x.OwnerId == Uid)
                ?? throw ApiException.NotFound("Property not found.");
        if (p.Status is ListingStatus.Approved or ListingStatus.Published)
            throw ApiException.Conflict("This listing is already verified.");
        if (!d.LegalDeclarationAccepted) throw ApiException.BadRequest("The legal declaration must be accepted.");
        if (!Regex.IsMatch(d.NationalIdNumber ?? "", @"^\d{14}$")) throw ApiException.BadRequest("National ID must be 14 digits.");
        if (string.IsNullOrWhiteSpace(d.DeedOrContractNumber)) throw ApiException.BadRequest("Deed or contract number is required.");
        if (await db.LegalVerifications.AnyAsync(v => v.PropertyId == propertyId &&
                v.ReviewStatus != VerificationReviewStatus.Rejected && v.ReviewStatus != VerificationReviewStatus.MoreInfoRequested))
            throw ApiException.Conflict("A verification is already in review for this listing.");

        var seq = await db.NextValueAsync("LegalRefSeq");
        var v = new LegalVerification
        {
            PropertyId = propertyId, OwnerId = Uid,
            ReferenceNumber = $"EGY-LEG-{DateTime.UtcNow.Year}-{seq:D4}",
            DocType = d.DocType, DeedOrContractNumber = d.DeedOrContractNumber.Trim(),
            DeveloperOrNotaryOffice = d.DeveloperOrNotaryOffice,
            NationalIdEncrypted = Protector.Protect(d.NationalIdNumber!),
            LegalDeclarationAccepted = true
        };
        p.Status = ListingStatus.PendingVerification;
        db.LegalVerifications.Add(v);
        await db.SaveChangesAsync();
        return Created($"/api/v1/verifications/{v.Id}", new { data = ShapeOwner(v) });
    }

    [Authorize(Policy = "Owner"), HttpPost("verifications/{id:guid}/documents")]
    [RequestSizeLimit(MaxFileBytes + 1024 * 64)]
    public async Task<IActionResult> Upload(Guid id, [FromForm] string category, IFormFile file)
    {
        var v = await db.LegalVerifications.Include(x => x.Documents).FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == Uid)
                ?? throw ApiException.NotFound("Verification not found.");
        if (!DocCategories.Contains(category)) throw ApiException.BadRequest("Unknown document category.");
        if (file is null || file.Length == 0) throw ApiException.BadRequest("File is empty.");
        if (file.Length > MaxFileBytes) throw ApiException.BadRequest("File exceeds 10 MB.");
        if (!AllowedTypes.TryGetValue(file.ContentType, out var exts) ||
            !exts.Contains(Path.GetExtension(file.FileName).ToLowerInvariant()))
            throw ApiException.BadRequest("Only PDF, JPG or PNG files are allowed.");
        if (v.Documents.Count >= 10) throw ApiException.BadRequest("Too many documents.");

        // Sniff magic bytes so a renamed executable can't masquerade as a PDF/image.
        await using (var peek = file.OpenReadStream())
        {
            var head = new byte[4];
            var n = await peek.ReadAsync(head);
            if (n < 4 || !MagicOk(file.ContentType, head)) throw ApiException.BadRequest("File content does not match its type.");
        }

        var root = Path.GetFullPath(cfg["Storage:PrivatePath"] ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "private"));
        Directory.CreateDirectory(root);
        var stored = $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName).ToLowerInvariant()}";   // never trust the client name on disk
        await using (var fs = System.IO.File.Create(Path.Combine(root, stored)))
            await file.CopyToAsync(fs);

        var doc = new VerificationDocument
        {
            LegalVerificationId = v.Id, Category = category, FileName = Path.GetFileName(file.FileName),
            ContentType = file.ContentType, SizeBytes = file.Length, StoragePath = stored
        };
        db.VerificationDocuments.Add(doc);
        await db.SaveChangesAsync();
        return Created($"/api/v1/verification-documents/{doc.Id}", new { data = new { doc.Id, doc.Category, doc.FileName, doc.ContentType, doc.SizeBytes, doc.UploadedAt } });
    }

    [AllowAnonymous, HttpGet("verification-documents/{id:guid}/file")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] long exp = 0, [FromQuery] string? sig = null)
    {
        var doc = await db.VerificationDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id)
                  ?? throw ApiException.NotFound("Document not found.");
        var v = await db.LegalVerifications.AsNoTracking().FirstAsync(x => x.Id == doc.LegalVerificationId);
        var signed = exp > 0 && signer.ValidDoc(id, exp, sig);
        if (!signed)
        {
            if (User.Identity?.IsAuthenticated != true) throw new ApiException(401, "unauthorized", "Sign in required.");
            if (v.OwnerId != Uid && !IsStaff) throw ApiException.Forbidden("Not allowed.");   // owner + staff only
        }
        var root = Path.GetFullPath(cfg["Storage:PrivatePath"] ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "private"));
        var path = Path.GetFullPath(Path.Combine(root, doc.StoragePath));
        if (!path.StartsWith(root) || !System.IO.File.Exists(path)) throw ApiException.NotFound("File missing.");
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "private, no-store";   // sensitive legal documents are never cached
        return PhysicalFile(path, doc.ContentType);                 // inline, so a PDF opens in the browser tab
    }

    [Authorize(Policy = "Owner"), HttpPost("verifications/{id:guid}/resubmit")]
    public async Task<IActionResult> Resubmit(Guid id)
    {
        var v = await db.LegalVerifications.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == Uid)
                ?? throw ApiException.NotFound("Verification not found.");
        if (v.ReviewStatus is not (VerificationReviewStatus.Rejected or VerificationReviewStatus.MoreInfoRequested))
            throw ApiException.Conflict("Only rejected or info-requested verifications can be resubmitted.");
        var p = await db.Properties.FirstAsync(x => x.Id == v.PropertyId);
        v.ReviewStatus = VerificationReviewStatus.Submitted;
        v.RejectionReason = null; v.MoreInfoQuery = null; v.SubmittedAt = DateTime.UtcNow;
        p.Status = ListingStatus.PendingVerification;
        await db.SaveChangesAsync();
        return Ok(new { data = ShapeOwner(v) });
    }

    // ---------- Staff ----------
    [Authorize(Policy = "Admin"), HttpGet("admin/verification-documents/{id:guid}/link")]
    public async Task<IActionResult> DocumentLink(Guid id)
    {
        var doc = await db.VerificationDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id) ?? throw ApiException.NotFound("Document not found.");
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = Uid, Action = "verification.document.view", TargetType = "listing", TargetId = doc.LegalVerificationId.ToString(),
            Details = $"{doc.Category}: {doc.FileName}", IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await db.SaveChangesAsync();
        return Ok(new { data = new { url = signer.DocUrl(id), expiresInSeconds = 600 } });
    }

    /// <summary>Same review actions as above, addressed by listing: the admin UI works with listings, not verification ids.</summary>
    [Authorize(Policy = "Admin"), HttpPost("admin/properties/{propertyId:guid}/verification/{verb}")]
    public async Task<IActionResult> ByProperty(Guid propertyId, string verb, ReasonDto dto)
    {
        var v = await db.LegalVerifications.Where(x => x.PropertyId == propertyId).OrderByDescending(x => x.SubmittedAt).FirstOrDefaultAsync()
                ?? throw ApiException.NotFound("This listing has no ownership verification to review.");
        return verb switch
        {
            "approve" => await Approve(v.Id),
            "reject" => await Reject(v.Id, dto),
            "request-info" => await RequestInfo(v.Id, dto),
            "publish" => await Publish(v.Id),
            _ => throw ApiException.NotFound("Unknown action.")
        };
    }

    [Authorize(Policy = "Admin"), HttpGet("admin/verifications")]
    public async Task<IActionResult> Queue([FromQuery] VerificationReviewStatus? status)
    {
        var q = db.LegalVerifications.AsNoTracking().Include(v => v.Documents).AsQueryable();
        if (status is { } s) q = q.Where(v => v.ReviewStatus == s);
        var list = await q.OrderBy(v => v.SubmittedAt).ToListAsync();
        var props = await db.Properties.AsNoTracking().Where(p => list.Select(v => v.PropertyId).Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        return Ok(new
        {
            data = list.Select(v => new
            {
                v.Id, v.ReferenceNumber, v.PropertyId, listingCode = props.GetValueOrDefault(v.PropertyId)?.ListingCode,
                propertyTitle = props.GetValueOrDefault(v.PropertyId)?.Title, v.OwnerId,
                docType = v.DocType.ToString(), v.DeedOrContractNumber,
                nationalIdMasked = Mask(v.NationalIdEncrypted),   // admins never see the full ID in lists
                reviewStatus = v.ReviewStatus.ToString(), v.SubmittedAt, v.ReviewedAt, v.ReviewedBy,
                v.RejectionReason, v.MoreInfoQuery,
                documents = v.Documents.Select(d => new { d.Id, d.Category, d.FileName, d.SizeBytes })
            }),
            meta = new { total = list.Count }
        });
    }

    public record ReasonDto(string? Reason);

    [Authorize(Policy = "Admin"), HttpPost("admin/verifications/{id:guid}/approve")]
    public Task<IActionResult> Approve(Guid id) => Review(id, "approve", null, async (v, p) =>
    {
        if (v.ReviewStatus is not (VerificationReviewStatus.Submitted or VerificationReviewStatus.DeedReview or VerificationReviewStatus.DeveloperCheck))
            throw ApiException.Conflict($"Cannot approve a {v.ReviewStatus} verification.");
        v.ReviewStatus = VerificationReviewStatus.Approved;
        p.Status = ListingStatus.Approved;
        // The deed and national ID were checked, so the owner earns the "verified owner" badge.
        var owner = await db.Users.FirstOrDefaultAsync(u => u.Id == p.OwnerId);
        if (owner is not null) owner.Verified = true;
        await Task.CompletedTask;
    });

    [Authorize(Policy = "Admin"), HttpPost("admin/verifications/{id:guid}/reject")]
    public Task<IActionResult> Reject(Guid id, ReasonDto dto) => Review(id, "reject", dto.Reason, async (v, p) =>
    {
        RequireText(dto.Reason, "A rejection reason of at least 5 characters is required.");
        if (v.ReviewStatus == VerificationReviewStatus.Approved) throw ApiException.Conflict("Already approved.");
        v.ReviewStatus = VerificationReviewStatus.Rejected;
        v.RejectionReason = dto.Reason!.Trim();
        p.Status = ListingStatus.Rejected;
        await Task.CompletedTask;
    });

    [Authorize(Policy = "Admin"), HttpPost("admin/verifications/{id:guid}/request-info")]
    public Task<IActionResult> RequestInfo(Guid id, ReasonDto dto) => Review(id, "request_changes", dto.Reason, async (v, p) =>
    {
        RequireText(dto.Reason, "A question of at least 5 characters is required.");
        if (v.ReviewStatus == VerificationReviewStatus.Approved) throw ApiException.Conflict("Already approved.");
        v.ReviewStatus = VerificationReviewStatus.MoreInfoRequested;
        v.MoreInfoQuery = dto.Reason!.Trim();
        p.Status = ListingStatus.Draft;
        await Task.CompletedTask;
    });

    [Authorize(Policy = "Admin"), HttpPost("admin/verifications/{id:guid}/publish")]
    public Task<IActionResult> Publish(Guid id) => Review(id, "publish", null, async (v, p) =>
    {
        if (v.ReviewStatus != VerificationReviewStatus.Approved || p.Status != ListingStatus.Approved)
            throw ApiException.Conflict("A listing can only be published after approval.");
        p.Status = ListingStatus.Published;
        p.PublishedAt = DateTime.UtcNow;   // alert dispatcher keys off this
        await Task.CompletedTask;
    });

    // ---------- helpers ----------
    private async Task<IActionResult> Review(Guid id, string action, string? reason, Func<LegalVerification, Property, Task> apply)
    {
        var v = await db.LegalVerifications.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Verification not found.");
        var p = await db.Properties.FirstAsync(x => x.Id == v.PropertyId);
        await apply(v, p);
        v.ReviewedBy = Uid; v.ReviewedAt = DateTime.UtcNow;
        db.ReviewHistory.Add(new ReviewHistory { PropertyId = p.Id, AdminId = Uid, Action = action, Reason = reason });
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = Uid, Action = $"verification.{action}", TargetType = "listing", TargetId = p.Id.ToString(),
            Details = reason ?? "", IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await db.SaveChangesAsync();
        return Ok(new { data = new { v.Id, reviewStatus = v.ReviewStatus.ToString(), listingStatus = p.Status.ToString() } });
    }

    private static void RequireText(string? s, string message)
    {
        if ((s?.Trim().Length ?? 0) < 5) throw ApiException.BadRequest(message);
    }

    private bool IsStaff => User.IsInRole("admin") || User.IsInRole("moderator");

    private string Mask(string encrypted)
    {
        try { var id = Protector.Unprotect(encrypted); return new string('*', id.Length - 4) + id[^4..]; }
        catch { return "**********"; }
    }

    private static bool MagicOk(string contentType, byte[] h) => contentType switch
    {
        "application/pdf" => h[0] == 0x25 && h[1] == 0x50 && h[2] == 0x44 && h[3] == 0x46,       // %PDF
        "image/png" => h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47,
        "image/jpeg" => h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF,
        _ => false
    };

    private static object ShapeOwner(LegalVerification v) => new
    {
        v.Id, v.PropertyId, v.ReferenceNumber, docType = v.DocType.ToString(),
        reviewStatus = v.ReviewStatus.ToString(), v.SubmittedAt, estimatedReviewHours = 24,
        v.RejectionReason, v.MoreInfoQuery
    };
}
