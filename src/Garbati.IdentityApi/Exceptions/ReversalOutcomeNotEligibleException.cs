namespace Garbati.IdentityApi.Exceptions;

// DESIGN.md Section 8.6, T-020 task file: "{id} refers to a decision whose operation was 'Reversal' and
// that was actually Approved ... 404 or 409, your call which, say why in the PR." Judgment call: the
// decision DOES exist (AuthorizationDecisionNotFoundException already covers "does not exist at all" with
// 404) - it just is not in a state this action accepts, so this is a conflict (409), matching the same
// "resource exists but wrong state for this request" convention PendingApprovalRequestNotPendingException
// already established in this codebase.
public class ReversalOutcomeNotEligibleException : Exception
{
    public ReversalOutcomeNotEligibleException()
    {
    }

    public ReversalOutcomeNotEligibleException(string message)
        : base(message)
    {
    }

    public ReversalOutcomeNotEligibleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
