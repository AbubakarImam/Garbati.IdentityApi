using Garbati.IdentityApi.Contracts.Requests;
using Microsoft.AspNetCore.Mvc.Filters;
using Serilog;

namespace Garbati.IdentityApi.Middleware;

// Adds the request's idempotency key to the per-request log line, so retries can be traced by key
// without logging request bodies.
public class IdempotencyKeyLoggingFilter(IDiagnosticContext diagnosticContext) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var request = context.ActionArguments.Values.OfType<IIdempotentRequest>().FirstOrDefault();
        if (request is not null)
        {
            diagnosticContext.Set("IdempotencyKey", request.IdempotencyKey);
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
