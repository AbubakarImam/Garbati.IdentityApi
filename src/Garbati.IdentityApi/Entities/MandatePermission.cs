namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.2. One row per operation a mandate permits. A unique (MandateId, Operation) index
// (IdentityDbContext) stops the same operation being granted twice on one mandate.
public class MandatePermission
{
    public long Id { get; set; }
    public long MandateId { get; set; }
    public MandateOperation Operation { get; set; }

    public Mandate Mandate { get; set; } = null!;
}
