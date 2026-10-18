using Garbati.IdentityApi.Contracts.Responses;

namespace Garbati.IdentityApi.Services;

// POST /v1/authorization/requests' two possible outcomes once Section 3.8's branch is taken into account
// (DESIGN.md Section 5.1): a single-holder account (RequiredApprovals <= 1) is granted immediately, same
// shape as before T-018 (AuthorizationResponse); a joint account opens a PendingApprovalRequest instead
// and returns its id/status, not a grant (PendingApprovalRequestResponse). A discriminated union, not one
// response type with nullable fields, so the controller (and anyone reading this code) cannot confuse
// "no grant yet" with "grant is null because something went wrong."
public abstract record DebitRequestResult
{
    private DebitRequestResult()
    {
    }

    public sealed record Granted(AuthorizationResponse Response) : DebitRequestResult;

    public sealed record Pending(PendingApprovalRequestResponse Response) : DebitRequestResult;
}
