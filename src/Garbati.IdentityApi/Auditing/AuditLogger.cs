using Garbati.IdentityApi.Data;
using Garbati.IdentityApi.Entities;

namespace Garbati.IdentityApi.Auditing;

public class AuditLogger(IdentityDbContext dbContext, IHttpContextAccessor httpContextAccessor) : IAuditLogger
{
    private const int MaxUserAgentLength = 512;

    public AuditEvent Record(AuditEvent entry)
    {
        // Request details are filled in here so callers only describe the business change.
        // Outside an HTTP request (e.g. tests, future background jobs) they stay null.
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            entry.ActorId ??= httpContext.User.Identity?.Name;
            entry.CorrelationId = httpContext.TraceIdentifier;
            entry.Endpoint = $"{httpContext.Request.Method} {httpContext.Request.Path}";
            entry.IpAddress = httpContext.Connection.RemoteIpAddress?.ToString();

            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            entry.UserAgent = userAgent.Length switch
            {
                0 => null,
                > MaxUserAgentLength => userAgent[..MaxUserAgentLength],
                _ => userAgent
            };
        }

        entry.CreatedAt = DateTimeOffset.UtcNow;
        dbContext.AuditEvents.Add(entry);
        return entry;
    }
}
