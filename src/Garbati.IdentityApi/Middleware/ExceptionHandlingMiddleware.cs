using Garbati.IdentityApi.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Garbati.IdentityApi.Middleware;

public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request cancelled by client: {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var (status, title) = Map(ex);
            var isExpected = status != StatusCodes.Status500InternalServerError;

            if (isExpected)
                logger.LogWarning(ex, "Request rejected ({Status}) {Method} {Path}", status, context.Request.Method, context.Request.Path);
            else
                logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);

            context.Response.StatusCode = status;

            var problem = new ProblemDetails
            {
                Status = status,
                Title = title,
                // Only expected business errors expose their message; unexpected ones must not leak internals.
                Detail = isExpected ? ex.Message : null,
                // Lets a client quote the id when reporting a problem; it matches the logs and audit rows.
                Extensions = { ["correlationId"] = context.TraceIdentifier }
            };

            // DESIGN.md Section 5.1: a denial carries a stable reason code and the decision id (already
            // persisted by AuthorizationService before this was thrown) - never the signed grant, which
            // was never issued on this path.
            if (ex is AuthorizationDeniedException authorizationDenied)
            {
                problem.Extensions["decisionId"] = authorizationDenied.DecisionId;
                problem.Extensions["reasonCode"] = authorizationDenied.ReasonCode;
            }

            // T-018: the same stable-reason-code contract, for a denial that has no decisionId because no
            // AuthorizationDecision was ever created (see JointApprovalDeniedException's own comment).
            if (ex is JointApprovalDeniedException jointApprovalDenied)
            {
                problem.Extensions["reasonCode"] = jointApprovalDenied.ReasonCode;
            }

            // contentType is passed explicitly: WriteAsJsonAsync would otherwise overwrite it with application/json.
            await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
        }
    }

    // Status-code mapping matches ARCHITECTURE.md Section 5: 400 validation (handled per-controller via
    // BadRequest, not here), 404 not found, 409 conflict, 422 business-rule failure, 500 unexpected.
    private static (int Status, string Title) Map(Exception ex) => ex switch
    {
        CustomerNotFoundException => (StatusCodes.Status404NotFound, "Customer not found."),
        MandateNotFoundException => (StatusCodes.Status404NotFound, "Mandate not found."),
        InvalidCustomerStatusTransitionException => (StatusCodes.Status409Conflict, "Customer status transition not allowed."),
        DuplicateIdempotencyKeyException => (StatusCodes.Status409Conflict, "Duplicate idempotency key."),
        DuplicateActiveMandateException => (StatusCodes.Status409Conflict, "Customer already has an active mandate on this account."),
        AuthorizationDeniedException => (StatusCodes.Status422UnprocessableEntity, "Authorization denied."),

        // T-018 additions (DESIGN.md Section 3.8).
        PendingApprovalRequestNotFoundException => (StatusCodes.Status404NotFound, "Pending approval request not found."),
        ApprovalPolicyNotFoundException => (StatusCodes.Status404NotFound, "Approval policy not found."),
        PendingApprovalRequestNotPendingException => (StatusCodes.Status409Conflict, "Pending approval request is not open for approval."),
        DuplicateApprovalException => (StatusCodes.Status409Conflict, "This holder has already approved this request."),
        InvalidApprovalThresholdException => (StatusCodes.Status400BadRequest, "Invalid RequiredApprovals value."),
        JointApprovalDeniedException => (StatusCodes.Status422UnprocessableEntity, "Joint approval denied."),

        // T-020 additions (DESIGN.md Section 8.6).
        AuthorizationDecisionNotFoundException => (StatusCodes.Status404NotFound, "Authorization decision not found."),
        ReversalOutcomeNotEligibleException => (StatusCodes.Status409Conflict, "This decision cannot accept a reversal outcome."),
        DuplicateReversalOutcomeException => (StatusCodes.Status409Conflict, "A reversal outcome has already been recorded for this decision."),

        // T-031 addition (DESIGN.md Section 5.1): a malformed pageToken, same 400-via-exception pattern
        // as InvalidApprovalThresholdException above.
        InvalidPageTokenException => (StatusCodes.Status400BadRequest, "Invalid pageToken."),

        // T-044 additions (garbati-platform D-26): PATCH /v1/customers/{customerId}/identity-numbers'
        // set-once rule, and RecordKycDecisionAsync's narrowly-scoped BVN/NIN cardinality check.
        IdentityNumberAlreadySetException => (StatusCodes.Status409Conflict, "Identity number already set."),
        KycTierEvidenceNotMetException => (StatusCodes.Status422UnprocessableEntity, "KYC tier evidence not met."),
        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
    };
}
