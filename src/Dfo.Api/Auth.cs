using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dfo.Domain;
using Dfo.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public class JwtOptions
{
    public string Issuer { get; set; } = "dfo";
    public string Audience { get; set; } = "dfo-web";
    // Override via user-secrets / env var Jwt__Key. Must be >= 32 chars.
    public string Key { get; set; } = "dev-only-key-change-me-0123456789abcdef";
    public int AccessMinutes { get; set; } = 15;
}

public class ApiException(int status, string slug, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Slug { get; } = slug;
    public static ApiException NotFound(string m) => new(404, "not-found", m);
    public static ApiException BadRequest(string m) => new(400, "bad-request", m);
    public static ApiException Forbidden(string m) => new(403, "forbidden", m);
    public static ApiException Conflict(string m) => new(409, "conflict", m);
}

public class TokenService(IOptions<JwtOptions> opts)
{
    public const string StampClaim = "sstamp";

    public string Create(AppUser u)
    {
        var o = opts.Value;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, u.Id),
            new Claim(ClaimTypes.NameIdentifier, u.Id),
            new Claim(ClaimTypes.Name, u.Name),
            new Claim(ClaimTypes.Email, u.Email ?? ""),
            new Claim(ClaimTypes.Role, u.Role.ToString().ToLowerInvariant()),
            // Lets the API reject access tokens issued before a password reset / role change / suspension.
            new Claim(StampClaim, u.SecurityStamp ?? "")
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(o.Issuer, o.Audience, claims,
            expires: DateTime.UtcNow.AddMinutes(o.AccessMinutes), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>Pure token rules, kept separate so they can be unit-tested without a database.</summary>
public static class RefreshPolicy
{
    public static readonly TimeSpan Sliding = TimeSpan.FromDays(14);     // each refresh extends the session...
    public static readonly TimeSpan Absolute = TimeSpan.FromDays(30);    // ...but never beyond this since login
    public static readonly TimeSpan RaceGrace = TimeSpan.FromSeconds(10);// two tabs refreshing at once is not an attack

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static DateTime ExpiryFor(DateTime now, DateTime familyCreatedAt)
    {
        var sliding = now + Sliding;
        var cap = familyCreatedAt + Absolute;
        return sliding < cap ? sliding : cap;
    }
}

public record RegisterDto(string Name, string Email, string Password, string? Phone, string? Role);
public record LoginDto(string Email, string Password);
public record ForgotPasswordDto(string Email);
public record ResetPasswordDto(string Email, string Token, string NewPassword);

[ApiController]
[Route("api/v1/auth")]
public class AuthController(UserManager<AppUser> users, TokenService tokens, DfoDbContext db, IWebHostEnvironment env, IConfiguration cfg,
    Dfo.Application.IEmailSender email, Microsoft.Extensions.Caching.Memory.IMemoryCache cache) : ControllerBase
{
    private const string CookieName = "dfo_refresh";

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        // Only buyer/owner can self-register; admin/moderator are assigned by admins.
        var role = string.Equals(dto.Role, "owner", StringComparison.OrdinalIgnoreCase) ? UserRole.Owner : UserRole.Buyer;
        var user = new AppUser { UserName = dto.Email, Email = dto.Email, Name = dto.Name, PhoneNumber = dto.Phone, Role = role };
        var result = await users.CreateAsync(user, dto.Password);
        if (!result.Succeeded)
            throw ApiException.BadRequest(string.Join("; ", result.Errors.Select(e => e.Description)));
        await StartSession(user);
        return Ok(new { data = new { token = tokens.Create(user), user = Shape(user) } });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginDto dto)
    {
        var user = await users.FindByEmailAsync(dto.Email);
        // One generic message for every failure, so the response never reveals which emails exist.
        var fail = new ApiException(401, "unauthorized", "Invalid credentials.");
        if (user is null || user.Suspended || await users.IsLockedOutAsync(user)) throw fail;
        if (!await users.CheckPasswordAsync(user, dto.Password))
        {
            await users.AccessFailedAsync(user);   // 5 failures -> temporary lockout
            throw fail;
        }
        await users.ResetAccessFailedCountAsync(user);
        await StartSession(user);
        return Ok(new { data = new { token = tokens.Create(user), user = Shape(user) } });
    }

    /// <summary>Exchange the refresh cookie for a new access token and a new (rotated) refresh cookie.</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        var raw = Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw)) throw Unauthorized401();

        var now = DateTime.UtcNow;
        var hash = RefreshPolicy.Hash(raw);
        var t = await db.RefreshTokens.FirstOrDefaultAsync(x => x.TokenHash == hash);
        if (t is null) { ClearCookie(); throw Unauthorized401(); }

        if (t.RevokedAt is not null || t.ExpiresAt <= now) { ClearCookie(); throw Unauthorized401(); }

        if (t.UsedAt is { } usedAt)
        {
            // A rotated token was presented again. Within a few seconds that is two tabs racing, so just refuse.
            if (now - usedAt <= RefreshPolicy.RaceGrace) throw Unauthorized401();
            // Later than that it was probably stolen and replayed: kill the whole login session.
            await RevokeFamily(t.FamilyId, now);
            ClearCookie();
            throw Unauthorized401();
        }

        var user = await users.FindByIdAsync(t.UserId);
        if (user is null || user.Suspended)
        {
            await RevokeFamily(t.FamilyId, now);
            ClearCookie();
            throw Unauthorized401();
        }

        // Claim the token atomically; if a concurrent request won, this one gets 0 rows and is refused.
        var claimed = await db.RefreshTokens.Where(x => x.Id == t.Id && x.UsedAt == null && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, now));
        if (claimed == 0) throw Unauthorized401();

        await IssueRefresh(user.Id, t.FamilyId, t.FamilyCreatedAt, now);
        return Ok(new { data = new { token = tokens.Create(user), user = Shape(user) } });   // current role, not the old one
    }

    /// <summary>Ends this device's session.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var raw = Request.Cookies[CookieName];
        if (!string.IsNullOrEmpty(raw))
        {
            var hash = RefreshPolicy.Hash(raw);
            var familyId = await db.RefreshTokens.Where(x => x.TokenHash == hash).Select(x => (Guid?)x.FamilyId).FirstOrDefaultAsync();
            if (familyId is { } f) await RevokeFamily(f, DateTime.UtcNow);
        }
        ClearCookie();
        return NoContent();
    }

    /// <summary>Ends every session of the signed-in user (all devices).</summary>
    [Authorize, HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll()
    {
        await RefreshTokenRevoker.RevokeAllForUser(db, User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        ClearCookie();
        return NoContent();
    }

    /// <summary>
    /// Always answers 202 with the same body, whether or not the email exists, so it cannot be used to
    /// discover who has an account. The email itself is sent in the background so response time doesn't leak it either.
    /// </summary>
    [HttpPost("forgot-password")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordDto dto)
    {
        var generic = Accepted(new { data = new { message = "If that email is registered, a reset link has been sent." } });
        var user = string.IsNullOrWhiteSpace(dto.Email) ? null : await users.FindByEmailAsync(dto.Email.Trim());
        if (user is null || user.Suspended) return generic;

        // One reset email per account per minute, so this endpoint can't be used to spam someone's inbox.
        if (!cache.TryGetValue($"pwreset:{user.Id}", out _))
        {
            cache.Set($"pwreset:{user.Id}", true, TimeSpan.FromMinutes(1));
            var token = await users.GeneratePasswordResetTokenAsync(user);   // single use: resetting changes the security stamp
            var link = Dfo.Application.ResetLink.Build(cfg["Frontend:BaseUrl"] ?? "http://localhost:4200", user.Email!, token);
            var to = user.Email!;
            var body = string.Join(Environment.NewLine + Environment.NewLine,
                $"Reset your DFO password (link valid for 1 hour):{Environment.NewLine}{link}",
                $"لإعادة تعيين كلمة المرور (الرابط صالح لمدة ساعة):{Environment.NewLine}{link}",
                "If you did not ask for this, ignore this email - your password has not changed.");
            _ = Task.Run(async () =>
            {
                try { await email.SendAsync(to, "Reset your DFO password", body); }
                catch (Exception) { /* a delivery failure must not surface to the requester */ }
            });
        }
        return generic;
    }

    [HttpPost("reset-password")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword(ResetPasswordDto dto)
    {
        var invalid = ApiException.BadRequest("This reset link is invalid or has expired. Please request a new one.");
        if (string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Token) || string.IsNullOrEmpty(dto.NewPassword)) throw invalid;
        var user = await users.FindByEmailAsync(dto.Email.Trim());
        if (user is null || user.Suspended) throw invalid;

        var result = await users.ResetPasswordAsync(user, dto.Token, dto.NewPassword);
        if (!result.Succeeded)
        {
            // Wrong/used/expired token -> generic message. Password-policy errors are only reachable with a valid token.
            if (result.Errors.Any(e => e.Code == "InvalidToken")) throw invalid;
            throw ApiException.BadRequest(string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        // The old password may be in an attacker's hands: end every session and clear any lockout.
        await RefreshTokenRevoker.RevokeAllForUser(db, user.Id);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        ClearCookie();
        var to = user.Email!;
        _ = Task.Run(async () =>
        {
            try { await email.SendAsync(to, "Your DFO password was changed", "Your password was just changed. If this was not you, reset it again and contact support."); }
            catch (Exception) { }
        });
        return NoContent();
    }

    [Authorize, HttpGet("/api/v1/me")]
    public async Task<IActionResult> Me()
    {
        var user = await users.FindByIdAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!)
                   ?? throw ApiException.NotFound("User not found.");
        return Ok(new { data = Shape(user) });
    }

    // ---------- helpers ----------
    private async Task StartSession(AppUser user)
    {
        var now = DateTime.UtcNow;
        // Housekeeping: drop this user's long-dead tokens so the table doesn't grow forever.
        await db.RefreshTokens.Where(x => x.UserId == user.Id && x.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync();
        await IssueRefresh(user.Id, Guid.NewGuid(), now, now);
    }

    private async Task IssueRefresh(string userId, Guid familyId, DateTime familyCreatedAt, DateTime now)
    {
        var raw = RefreshPolicy.NewToken();
        var expires = RefreshPolicy.ExpiryFor(now, familyCreatedAt);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId, TokenHash = RefreshPolicy.Hash(raw), FamilyId = familyId,
            FamilyCreatedAt = familyCreatedAt, CreatedAt = now, ExpiresAt = expires,
            CreatedByIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await db.SaveChangesAsync();

        Response.Cookies.Append(CookieName, raw, CookieOptions(expires));
    }

    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,                         // not readable by page JavaScript, so XSS cannot steal it
        Secure = !env.IsDevelopment(),           // HTTPS only outside development
        SameSite = Enum.TryParse<SameSiteMode>(cfg["Auth:CookieSameSite"], true, out var m) ? m : SameSiteMode.Strict,
        Path = "/api/v1/auth",                   // only sent to the auth endpoints
        Expires = expires,
        IsEssential = true
    };

    private void ClearCookie() => Response.Cookies.Delete(CookieName, CookieOptions(null));

    private Task RevokeFamily(Guid familyId, DateTime now) =>
        db.RefreshTokens.Where(x => x.FamilyId == familyId && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now));

    private static ApiException Unauthorized401() => new(401, "unauthorized", "Session expired. Please sign in again.");

    private static object Shape(AppUser u) => new
    {
        u.Id, u.Name, email = u.Email, role = u.Role.ToString().ToLowerInvariant(),
        u.Verified, u.Avatar, u.Title, phone = u.PhoneNumber
    };
}

public static class RefreshTokenRevoker
{
    public static Task<int> RevokeAllForUser(DfoDbContext db, string userId) =>
        db.RefreshTokens.Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow));
}
