namespace Dfo.Domain;

public enum DocType { RegisteredDeed, DeveloperContract, CourtValidity, PowerOfAttorney }
public enum VerificationReviewStatus { Submitted, DeedReview, DeveloperCheck, Approved, Rejected, MoreInfoRequested }

public class AvailabilityRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = "";
    public Guid? PropertyId { get; set; }
    public int DayOfWeek { get; set; }          // 0 = Sunday
    public TimeOnly StartTime { get; set; }     // Africa/Cairo wall-clock
    public TimeOnly EndTime { get; set; }
    public int SlotDurationMin { get; set; } = 30;
}

public class AvailabilityException
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = "";
    public Guid? PropertyId { get; set; }
    public DateOnly Date { get; set; }
    public bool Blocked { get; set; } = true;   // false = custom window
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public int? SlotDurationMin { get; set; }
    public string? Reason { get; set; }
}

public class Viewing
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PropertyId { get; set; }
    public string BuyerId { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public DateTime StartsAt { get; set; }      // UTC
    public DateTime EndsAt { get; set; }        // UTC
    public ViewingStatus Status { get; set; } = ViewingStatus.Requested;
    public string? MeetingArea { get; set; }
    public string? MeetingPoint { get; set; }   // exposed only when Confirmed
    public string? BuyerNotes { get; set; }
    public string? OwnerNotes { get; set; }
    public string? DeclineReason { get; set; }
    public string? CancellationReason { get; set; }
    public string? RescheduleReason { get; set; }
    public DateTime? RescheduleProposedStartsAt { get; set; }
    public DateTime? RescheduleProposedEndsAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class LegalVerification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PropertyId { get; set; }
    public string OwnerId { get; set; } = "";
    public string ReferenceNumber { get; set; } = "";   // EGY-LEG-YYYY-####
    public DocType DocType { get; set; }
    public string DeedOrContractNumber { get; set; } = "";
    public string? DeveloperOrNotaryOffice { get; set; }
    public string NationalIdEncrypted { get; set; } = ""; // 14 digits, protected at rest
    public bool LegalDeclarationAccepted { get; set; }
    public VerificationReviewStatus ReviewStatus { get; set; } = VerificationReviewStatus.Submitted;
    public string? RejectionReason { get; set; }
    public string? MoreInfoQuery { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public List<VerificationDocument> Documents { get; set; } = new();
}

public class VerificationDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LegalVerificationId { get; set; }
    public string Category { get; set; } = "deed";  // deed | national_id | maintenance_clearance | power_of_attorney
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string StoragePath { get; set; } = "";   // private storage, never a public URL
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

public class ReviewHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PropertyId { get; set; }
    public string AdminId { get; set; } = "";
    public string Action { get; set; } = "";   // approve | reject | request_changes | publish ...
    public string? Reason { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
