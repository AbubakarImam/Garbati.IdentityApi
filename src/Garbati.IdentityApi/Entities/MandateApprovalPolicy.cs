namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.2a / decision #20 - shape only, not the joint-account workflow (that is T-018's
// Section 3.8). Created the moment an account's first mandate is created (one row per LedgerAccountId).
// RequiredApprovals is always 1 in this task: there is no endpoint yet to set it otherwise, and nothing
// in T-017 reads it as anything other than exactly 1 (see AuthorizationService - a RequiredApprovals
// value other than 1 is a defensive denial, never a branch that tries to handle it).
public class MandateApprovalPolicy
{
    public long Id { get; set; }
    public string LedgerAccountId { get; set; } = null!;
    public int RequiredApprovals { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastChangedAt { get; set; }
    public string? LastChangedBy { get; set; }
}
