using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;

namespace Garbati.IdentityApi.Services;

// Named ITransactionAuthorizationService, not IAuthorizationService, to avoid colliding with ASP.NET
// Core's own Microsoft.AspNetCore.Authorization.IAuthorizationService (an unrelated type this project
// also references via [Authorize] policies).
public interface ITransactionAuthorizationService
{
    // Throws AuthorizationDeniedException on denial - never returns a "denied" AuthorizationResponse,
    // so there is no code path where a caller could mistakenly treat a denial as a grant.
    Task<AuthorizationResponse> RequestAuthorizationAsync(
        AuthorizationRequest request, CancellationToken cancellationToken = default);

    Task<AuthorizationDecisionResponse?> GetDecisionAsync(string decisionId, CancellationToken cancellationToken = default);

    // T-018 additions: the two halves of the single-holder grant-issuance logic (3.2), exposed so the
    // joint-approval flow (3.8.1 phase 2) can reuse them once a PendingApprovalRequest's threshold is met,
    // instead of duplicating the sign-and-persist code. Both assume the caller has ALREADY established
    // eligibility (MandateEligibility.CheckAsync) and, for IssueApprovedGrantAsync, the live mandate
    // re-check (3.8.5) - neither method re-runs any of that itself.
    Task<AuthorizationResponse> IssueApprovedGrantAsync(
        string decisionId, AuthorizationRequest request, string mandateId, DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task RecordDenialAsync(
        string decisionId, AuthorizationRequest request, string reasonCode, DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
