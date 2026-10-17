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
        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
    };
}
