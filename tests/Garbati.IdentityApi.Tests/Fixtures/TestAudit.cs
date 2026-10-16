using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Data;
using Microsoft.AspNetCore.Http;

namespace Garbati.IdentityApi.Tests.Fixtures;

// Service tests run outside an HTTP request, so request details on audit rows stay null.
public static class TestAudit
{
    public static IAuditLogger For(IdentityDbContext ctx) => new AuditLogger(ctx, new HttpContextAccessor());
}
