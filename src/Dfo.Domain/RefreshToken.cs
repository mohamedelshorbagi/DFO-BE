namespace Dfo.Domain;

/// <summary>
/// One link in a rotating refresh-token chain. Only the SHA-256 hash is stored, so a database
/// leak does not hand out usable tokens. All tokens issued from one login share a FamilyId.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public Guid FamilyId { get; set; }
    public DateTime FamilyCreatedAt { get; set; }   // absolute cap on a session, however often it is refreshed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }           // set when exchanged for a new token
    public DateTime? RevokedAt { get; set; }
    public string? CreatedByIp { get; set; }
}
