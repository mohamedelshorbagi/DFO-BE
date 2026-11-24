namespace Dfo.Domain;

public enum AlertFrequency { Instant, Daily, Weekly }
public enum ReportStatus { Open, Investigating, Resolved, Dismissed, Escalated }

public class SearchAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public bool NotifyWhatsApp { get; set; }
    public AlertFrequency Frequency { get; set; } = AlertFrequency.Daily;
    public bool IsActive { get; set; } = true;
    // criteria (null = any)
    public Purpose? Purpose { get; set; }
    public Category? Category { get; set; }
    public string? City { get; set; }
    public string? Type { get; set; }
    public string? Query { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public int MatchCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastNotifiedAt { get; set; }   // last time an email actually went out
    public DateTime? LastCheckedAt { get; set; }    // last dispatcher pass; listings published after this are "new"
}

public class Report
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TargetType { get; set; } = "listing";   // listing | user | chat_circumvention
    public string TargetId { get; set; } = "";
    public string ReporterId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Description { get; set; } = "";
    public ReportStatus Status { get; set; } = ReportStatus.Open;
    public string? AdminNotes { get; set; }
    public string? ActionTaken { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

public class MarketplaceSettings
{
    public int Id { get; set; } = 1;   // single row
    public bool RequireEgyptianDeed { get; set; } = true;
    public bool MandatoryNationalId { get; set; } = true;
    public bool AutoMaskContactInfo { get; set; } = true;
    public bool AllowDirectInstantPublish { get; set; }
    public int MaxImagesPerListing { get; set; } = 15;
    public string MarketplaceName { get; set; } = "DFO";
    public decimal MarketplaceCommissionPercent { get; set; }
    public string SupportEmail { get; set; } = "";
    public string SupportPhone { get; set; } = "";
    public bool EmailNotificationsEnabled { get; set; } = true;
    public bool SmsNotificationsEnabled { get; set; }
    public bool MaintenanceMode { get; set; }
}

public class TaxonomyItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "";     // propertyType | finishing | amenity | city | compound
    public string Key { get; set; } = "";
    public string En { get; set; } = "";
    public string Ar { get; set; } = "";
    public string? Category { get; set; }      // residential/commercial for types; amenity group
    public string? Icon { get; set; }
    public string? ParentKey { get; set; }     // compound -> city key
}
