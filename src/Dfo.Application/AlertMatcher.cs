using Dfo.Domain;

namespace Dfo.Application;

public static class AlertMatcher
{
    /// <summary>Mirrors the frontend's matchesListing(): every criterion that is set must match.</summary>
    public static bool Matches(SearchAlert a, Property p)
    {
        if (p.Status != ListingStatus.Published) return false;
        if (a.Purpose is { } pu && p.Purpose != pu) return false;
        if (a.Category is { } ca && p.Category != ca) return false;
        if (!string.IsNullOrWhiteSpace(a.City) && !string.Equals(a.City, p.City, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(a.Type) && !string.Equals(a.Type, p.Type, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.MinPrice is { } min && p.Price < min) return false;
        if (a.MaxPrice is { } max && p.Price > max) return false;
        if (!string.IsNullOrWhiteSpace(a.Query))
        {
            var hay = $"{p.Title} {p.TitleAr} {p.Location} {p.Compound} {p.ListingCode}";
            if (!hay.Contains(a.Query.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Is this alert due for a send at <paramref name="now"/>? Instant alerts are always due; digests wait for their window.</summary>
    public static bool IsDue(SearchAlert a, DateTime now) => a.IsActive && a.Frequency switch
    {
        AlertFrequency.Instant => true,
        AlertFrequency.Daily => a.LastCheckedAt is null || now - a.LastCheckedAt >= TimeSpan.FromHours(24),
        AlertFrequency.Weekly => a.LastCheckedAt is null || now - a.LastCheckedAt >= TimeSpan.FromDays(7),
        _ => false
    };
}

public record AlertMessage(string Email, string? WhatsAppPhone, string Subject, string Body);

/// <summary>Delivery channel. The default implementation only logs; swap for SMTP/SendGrid/WhatsApp later.</summary>
public interface IAlertNotifier
{
    Task SendAsync(AlertMessage message, CancellationToken ct);
}
