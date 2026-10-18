namespace Garbati.IdentityApi.Exceptions;

// Thrown for GET/POST .../approval-policy[/change-requests] against an account IdentityApi has never
// seen a mandate for - a MandateApprovalPolicy is only ever created alongside an account's first mandate
// (DESIGN.md Section 2.2a), so "no policy" means "no mandate has ever been attached to this account."
public class ApprovalPolicyNotFoundException : Exception
{
    public ApprovalPolicyNotFoundException()
    {
    }

    public ApprovalPolicyNotFoundException(string message)
        : base(message)
    {
    }

    public ApprovalPolicyNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
