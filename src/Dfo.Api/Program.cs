using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dfo.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services.AddDbContext<DfoDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.Password.RequiredLength = 8;
        o.User.RequireUniqueEmail = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<DfoDbContext>()
    .AddDefaultTokenProviders();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddScoped<TokenService>();

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        // A JWT stays valid until it expires, so re-check the user on every request:
        // suspended users and users whose role changed must not keep their old access.
        o.Events = new JwtBearerEvents
        {
            // Browsers can't set headers on a WebSocket handshake, so the SignalR client sends the token in the query string.
            // Accepted ONLY for the hub path; every normal API call must still use the Authorization header.
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/api/v1/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
            OnTokenValidated = async ctx =>
            {
                var id = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var role = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
                var db = ctx.HttpContext.RequestServices.GetRequiredService<DfoDbContext>();
                var stamp = ctx.Principal?.FindFirst(TokenService.StampClaim)?.Value;
                var u = id is null ? null : await db.Users.AsNoTracking()
                    .Where(x => x.Id == id).Select(x => new { x.Suspended, x.Role, x.SecurityStamp }).FirstOrDefaultAsync();
                // The security stamp changes on password reset, role change and suspension, which ends old access tokens at once.
                if (u is null || u.Suspended || stamp != u.SecurityStamp ||
                    !string.Equals(u.Role.ToString(), role, StringComparison.OrdinalIgnoreCase))
                    ctx.Fail("Session is no longer valid.");
            }
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Owner", p => p.RequireRole("owner"))
    .AddPolicy("Admin", p => p.RequireRole("admin", "moderator"));

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:4200"])
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddMemoryCache();
// Real SMTP when Smtp:Host is configured; otherwise the logging stand-in (fine for local development).
var smtp = builder.Configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
smtp.Validate();   // bad config stops startup with a clear message instead of failing on the first reset email
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
if (smtp.IsConfigured) builder.Services.AddSingleton<Dfo.Application.IEmailSender, SmtpEmailSender>();
else
{
    builder.Services.AddSingleton<Dfo.Application.IEmailSender, LoggingEmailSender>();
    if (!builder.Environment.IsDevelopment())
        Console.Error.WriteLine("WARNING: Smtp:Host is not set - password-reset and alert emails will NOT be delivered.");
}
// Reset links die after an hour (Identity's default is a day).
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));
// Per-IP limits on credential endpoints. Behind a proxy, enable ForwardedHeaders so this sees the real client IP.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    // Messages and inquiries: 30 a minute per signed-in user (falls back to IP), enough to chat, not enough to flood.
    o.AddPolicy("chat", ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("auth", ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddSignalR();
builder.Services.AddScoped<ChatDomain>();
builder.Services.AddScoped<ChatShaper>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<MediaUrlSigner>();
builder.Services.AddScoped<PropertyShaper>();
builder.Services.AddSingleton<Dfo.Application.IAlertNotifier, EmailAlertNotifier>();
builder.Services.AddHostedService<AlertDispatcher>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// RFC 7807 errors with the frontend's problem "type" URLs.
app.UseExceptionHandler(a => a.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, slug) = ex switch
    {
        ApiException api => (api.Status, api.Slug),
        _ => (500, "server-error")
    };
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/problem+json";
    await ctx.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Type = $"https://directrealestate.eg/problems/{slug}",
        Title = slug.Replace('-', ' '),
        Status = status,
        Detail = status == 500 ? "Unexpected error." : ex?.Message,
        Instance = ctx.Request.Path
    });
}));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// First-admin bootstrap. Credentials come only from config / env (Seed__AdminEmail, Seed__AdminPassword);
// nothing is hardcoded, and nothing happens when they are unset.
{
    var email = app.Configuration["Seed:AdminEmail"];
    var pwd = app.Configuration["Seed:AdminPassword"];
    if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(pwd))
    {
        using var scope = app.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await um.FindByEmailAsync(email) is null)
        {
            var admin = new AppUser { UserName = email, Email = email, Name = "Administrator", Role = Dfo.Domain.UserRole.Admin, Verified = true };
            var r = await um.CreateAsync(admin, pwd);
            app.Logger.LogInformation("Seeded admin {Email}: {Result}", email, r.Succeeded ? "ok" : string.Join("; ", r.Errors.Select(e => e.Code)));
        }
    }
}

// Default reference data (cities, compounds, property types...) on a fresh database.
{
    using var scope = app.Services.CreateScope();
    try { await TaxonomySeeder.SeedIfEmpty(scope.ServiceProvider.GetRequiredService<DfoDbContext>()); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Could not seed taxonomies (is the database migrated?)"); }
}

app.UseSerilogRequestLogging();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();   // after authentication, so per-user limits (chat) can see who is calling
app.UseAuthorization();
app.MapControllers();
app.MapHub<ChatHub>("/api/v1/hubs/chat");

app.Run();

public partial class Program;
