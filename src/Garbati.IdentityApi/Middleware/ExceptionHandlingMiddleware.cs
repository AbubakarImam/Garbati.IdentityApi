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
            // contentType is passed explicitly: WriteAsJsonAsync would otherwise overwrite it with application/json.
            await context.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = status,
                    Title = title,
                    // Only expected business errors expose their message; unexpected ones must not leak internals.
                    Detail = isExpected ? ex.Message : null,
                    // Lets a client quote the id when reporting a problem; it matches the logs and audit rows.
                    Extensions = { ["correlationId"] = context.TraceIdentifier }
                },
                options: null,
                contentType: "application/problem+json");
        }
    }

    // Status-code mapping matches ARCHITECTURE.md Section 5: 400 validation (handled per-controller via
    // BadRequest, not here), 404 not found, 409 conflict, 422 business-rule failure, 500 unexpected.
    private static (int Status, string Title) Map(Exception ex) => ex switch
    {
        CustomerNotFoundException => (StatusCodes.Status404NotFound, "Customer not found."),
        InvalidCustomerStatusTransitionException => (StatusCodes.Status409Conflict, "Customer status transition not allowed."),
        DuplicateIdempotencyKeyException => (StatusCodes.Status409Conflict, "Duplicate idempotency key."),
        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
    };
}
