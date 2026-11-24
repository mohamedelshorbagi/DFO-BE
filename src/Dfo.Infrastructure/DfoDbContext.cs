using Dfo.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Dfo.Infrastructure;

public class AppUser : IdentityUser
{
    public string Name { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Buyer;
    public bool Verified { get; set; }
    public bool Suspended { get; set; }
    public string? SuspendedReason { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public string? Avatar { get; set; }
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DfoDbContext(DbContextOptions<DfoDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<PropertyImage> PropertyImages => Set<PropertyImage>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<ChatThread> ChatThreads => Set<ChatThread>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AvailabilityRule> AvailabilityRules => Set<AvailabilityRule>();
    public DbSet<AvailabilityException> AvailabilityExceptions => Set<AvailabilityException>();
    public DbSet<Viewing> Viewings => Set<Viewing>();
    public DbSet<LegalVerification> LegalVerifications => Set<LegalVerification>();
    public DbSet<VerificationDocument> VerificationDocuments => Set<VerificationDocument>();
    public DbSet<ReviewHistory> ReviewHistory => Set<ReviewHistory>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SearchAlert> SearchAlerts => Set<SearchAlert>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<MarketplaceSettings> Settings => Set<MarketplaceSettings>();
    public DbSet<TaxonomyItem> Taxonomies => Set<TaxonomyItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.HasSequence<int>("ListingCodeSeq").StartsAt(1);

        b.Entity<Property>(e =>
        {
            e.HasIndex(p => p.ListingCode).IsUnique();
            e.HasIndex(p => new { p.Status, p.Purpose, p.City, p.Type, p.Price });
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.Property(p => p.DownPayment).HasPrecision(18, 2);
            e.Property(p => p.Purpose).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.Category).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(p => p.Availability).HasConversion<string>().HasMaxLength(32);
            e.HasMany(p => p.Images).WithOne().HasForeignKey(i => i.PropertyId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Favorite>().HasKey(f => new { f.UserId, f.PropertyId });
        b.Entity<Lead>().Property(l => l.BudgetEgp).HasPrecision(18, 2);
        b.Entity<Lead>().Property(l => l.Status).HasConversion<string>().HasMaxLength(32);

        b.Entity<ChatThread>(e =>
        {
            e.HasIndex(t => new { t.PropertyId, t.BuyerId }).IsUnique();
            e.HasMany(t => t.Messages).WithOne().HasForeignKey(m => m.ThreadId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ChatMessage>(e =>
        {
            e.HasIndex(m => new { m.ThreadId, m.Timestamp });
            e.Property(m => m.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<Viewing>(e =>
        {
            e.Property(v => v.Status).HasConversion<string>().HasMaxLength(32);
            // Hard guarantee against double booking, even under concurrent requests.
            e.HasIndex(v => new { v.PropertyId, v.StartsAt }).IsUnique()
                .HasFilter("[Status] IN ('Requested','Confirmed','RescheduleRequested')");
            e.HasIndex(v => v.BuyerId);
            e.HasIndex(v => v.OwnerId);
        });

        b.Entity<LegalVerification>(e =>
        {
            e.HasIndex(v => v.ReferenceNumber).IsUnique();
            e.HasIndex(v => v.PropertyId);
            e.Property(v => v.DocType).HasConversion<string>().HasMaxLength(32);
            e.Property(v => v.ReviewStatus).HasConversion<string>().HasMaxLength(32);
            e.HasMany(v => v.Documents).WithOne().HasForeignKey(d => d.LegalVerificationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.HasSequence<int>("LegalRefSeq").StartsAt(1);

        b.Entity<SearchAlert>(e =>
        {
            e.HasIndex(a => a.UserId);
            e.Property(a => a.Frequency).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.Purpose).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.Category).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.MinPrice).HasPrecision(18, 2);
            e.Property(a => a.MaxPrice).HasPrecision(18, 2);
        });
        b.Entity<Report>(e =>
        {
            e.HasIndex(r => r.Status);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        });
        b.Entity<MarketplaceSettings>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.MarketplaceCommissionPercent).HasPrecision(5, 2);
        });
        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.HasIndex(t => t.FamilyId);
            e.Property(t => t.TokenHash).HasMaxLength(64);
        });
        b.Entity<TaxonomyItem>().HasIndex(t => new { t.Kind, t.Key }).IsUnique();
        b.Entity<AuditLog>().HasIndex(a => a.Timestamp);

        b.Entity<AppUser>().Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
    }
}
