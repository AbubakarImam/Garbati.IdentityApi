using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;

namespace Garbati.IdentityApi.Services;

// DESIGN.md Section 8 (decisions #30-#35), garbati-platform T-020. Deliberately separate from
// ITransactionAuthorizationService/IJointApprovalService: a Reversal request never touches the mandate/
// MandateApprovalPolicy model at all (decision #31), so there is no eligibility check or threshold logic
// to share with those two - only the grant-signing/persistence SHAPE is shared (see
// ReversalAuthorizationService's own comment for exactly how).
public interface IReversalAuthorizationService
{
    // Always either returns a granted AuthorizationResponse or throws - by the time this is called, the
    // controller has already confirmed the caller holds identity.ops.reversal and the validator has
    // already confirmed the reason code is one of the five valid values (DESIGN.md Section 8.3), so there
    // is no further denial path inside this method (decision #30/#31's whole point: nothing left to check).
    Task<AuthorizationResponse> RequestReversalAsync(
        AuthorizationRequest request, CancellationToken cancellationToken = default);

    Task<ReversalOutcomeResponse> RecordOutcomeAsync(
        string decisionId, RecordReversalOutcomeRequest request, CancellationToken cancellationToken = default);
}
