using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Entities;

namespace Garbati.IdentityApi.Services;

// DESIGN.md Section 3.8. Owns the branch POST /v1/authorization/requests must make on
// MandateApprovalPolicy.RequiredApprovals (5.1), the new .../approve endpoint, and account-level
// threshold reconfiguration (3.8.4). Named separately from ITransactionAuthorizationService (not folded
// into it) because its job is fundamentally different: that service answers "is this one request
// authorized, right now"; this one manages a multi-step, stateful consent process that may span several
// separate calls from different holders before an authorization decision is even attempted.
public interface IJointApprovalService
{
    Task<DebitRequestResult> RequestDebitAsync(AuthorizationRequest request, CancellationToken cancellationToken = default);

    Task<PendingApprovalRequestResponse> ApproveAsync(
        string requestId, ApproveRequest request, CancellationToken cancellationToken = default);

    Task<PendingApprovalRequestResponse?> GetPendingRequestAsync(string requestId, CancellationToken cancellationToken = default);

    Task<ApprovalPolicyResponse?> GetApprovalPolicyAsync(string ledgerAccountId, CancellationToken cancellationToken = default);

    Task<PendingApprovalRequestResponse> RequestThresholdChangeAsync(
        string ledgerAccountId, ChangeApprovalPolicyRequest request, CancellationToken cancellationToken = default);

    // T-031 (garbati-platform): a browsable queue of PendingApprovalRequest rows for a given Status, for
    // an ops-console "approvals queue" screen (there is no such screen yet - OpsApi/T-028 is the first
    // intended caller). Deliberately scoped to PendingApprovalRequest only, same as GetPendingRequestAsync
    // above - a single-holder (RequiredApprovals <= 1) request never creates a row here at all (it
    // resolves synchronously via AuthorizationDecision, see RequestDebitAsync's own branch), so this never
    // lists those. pageToken is the opaque cursor returned as PagedResponse.NextPageToken; null/omitted
    // starts from the beginning.
    Task<PagedResponse<PendingApprovalRequestResponse>> ListPendingRequestsAsync(
        PendingApprovalRequestStatus status, int pageSize, string? pageToken, CancellationToken cancellationToken = default);
}
