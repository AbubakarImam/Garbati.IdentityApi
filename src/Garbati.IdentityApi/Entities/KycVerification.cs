namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.3: append-only record of verification attempts and their results, kept separate
// from the customer's current KycStatus/KycTier (which live on Customer itself).
public class KycVerification
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public string VerificationType { get; set; } = null!;
    public string Provider { get; set; } = null!;
    public string? ProviderReference { get; set; }
    public KycStatus Status { get; set; }
    public string? ResultCode { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Customer Customer { get; set; } = null!;
}
