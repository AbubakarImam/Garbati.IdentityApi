using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Garbati.IdentityApi.Tests.Auditing;

[Collection("Postgres")]
public class AuditLoggerTests(PostgresFixture fixture)
{
    private static AuditEvent NewEntry() => new()
    {
        Action = AuditActions.CustomerCreated,
        EntityType = nameof(Customer),
        EntityId = "CUS-TEST",
    };

    [Fact]
    public void Record_FillsRequestDetails_FromTheCurrentRequest()
    {
        using var ctx = fixture.CreateContext();
        var httpContext = new DefaultHttpContext { TraceIdentifier = "corr-123" };
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/v1/customers";
        httpContext.Request.Headers.UserAgent = "identity-tests/1.0";
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7");
        httpContext.User = new ClaimsPrincipal(new GenericIdentity("ops-user"));
        var logger = new AuditLogger(ctx, new HttpContextAccessor { HttpContext = httpContext });

        var entry = logger.Record(NewEntry());

        Assert.Equal("ops-user", entry.ActorId);
        Assert.Equal("corr-123", entry.CorrelationId);
        Assert.Equal("POST /v1/customers", entry.Endpoint);
        Assert.Equal("10.0.0.7", entry.IpAddress);
        Assert.Equal("identity-tests/1.0", entry.UserAgent);
        // Staged only: the caller's SaveChanges writes it.
        Assert.Equal(EntityState.Added, ctx.Entry(entry).State);
    }

    [Fact]
    public void Record_LeavesRequestDetailsNull_OutsideAnHttpRequest()
    {
        using var ctx = fixture.CreateContext();

        var entry = TestAudit.For(ctx).Record(NewEntry());

        Assert.Null(entry.CorrelationId);
        Assert.Null(entry.Endpoint);
        Assert.Null(entry.IpAddress);
        Assert.NotEqual(default, entry.CreatedAt);
    }

    [Fact]
    public void Record_TruncatesAnOversizedUserAgent()
    {
        using var ctx = fixture.CreateContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.UserAgent = new string('x', 2000);
        var logger = new AuditLogger(ctx, new HttpContextAccessor { HttpContext = httpContext });

        var entry = logger.Record(NewEntry());

        Assert.Equal(512, entry.UserAgent!.Length);
    }
}
