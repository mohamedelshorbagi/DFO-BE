namespace Dfo.Domain;

public enum UserRole { Buyer, Owner, Admin, Moderator }
public enum Purpose { Sale, Rent }
public enum Category { Residential, Commercial }
public enum ListingStatus { Draft, PendingVerification, Approved, Published, Rejected, Paused, Archived }
public enum Availability { Available, UnderOffer, SoldOrRented }
public enum LeadStatus { New, Contacted, ViewingScheduled, Closed }
public enum MessageStatus { Sending, Sent, Delivered, Read }
public enum ViewingStatus { Requested, Confirmed, Declined, RescheduleRequested, Cancelled, Completed }

public class Property
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ListingCode { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string Title { get; set; } = "";
    public string TitleAr { get; set; } = "";
    public Purpose Purpose { get; set; }
    public Category Category { get; set; }
    public string Type { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? DownPayment { get; set; }
    public int? InstallmentYears { get; set; }
    public string Location { get; set; } = "";
    public string? LocationAr { get; set; }
    public string City { get; set; } = "";
    public string? Compound { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public int Bedrooms { get; set; }
    public int Bathrooms { get; set; }
    public double Area { get; set; }
    public string? Finishing { get; set; }
    public string? Delivery { get; set; }
    public string Description { get; set; } = "";
    public string AmenitiesJson { get; set; } = "[]";   // JSON array of strings; small, always read with the listing
    public bool Featured { get; set; }
    public bool Flagged { get; set; }
    public DateTime? PublishedAt { get; set; }
    public ListingStatus Status { get; set; } = ListingStatus.Draft;
    public Availability Availability { get; set; } = Availability.Available;
    public int Likes { get; set; }
    public int ViewsCount { get; set; }
    public int LeadsCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<PropertyImage> Images { get; set; } = new();
}

public class PropertyImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PropertyId { get; set; }
    public string StorageKey { get; set; } = "";   // base file name; <key>.hi.jpg and <key>.lo.jpg on disk
    public int Width { get; set; }
    public int Height { get; set; }
    public int SortOrder { get; set; }
    public bool IsCover { get; set; }
}

public class Favorite
{
    public string UserId { get; set; } = "";
    public Guid PropertyId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Lead
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PropertyId { get; set; }
    public string BuyerId { get; set; } = "";
    public Guid? ThreadId { get; set; }          // the chat thread the inquiry started; owners reply there
    public decimal? BudgetEgp { get; set; }
    public string Message { get; set; } = "";
    public LeadStatus Status { get; set; } = LeadStatus.New;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ChatThread
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PropertyId { get; set; }
    public string BuyerId { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public int BuyerUnread { get; set; }
    public int OwnerUnread { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<ChatMessage> Messages { get; set; } = new();
}

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ThreadId { get; set; }
    public string SenderId { get; set; } = "";
    public string SenderRole { get; set; } = "buyer"; // buyer | owner | system
    public string Text { get; set; } = "";
    public bool HasViolation { get; set; }
    public string? WarningNotice { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.Sent;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ActorId { get; set; } = "";
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Details { get; set; } = "";
    public string? IpAddress { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
