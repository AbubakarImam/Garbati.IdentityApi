using System.Net;
using System.Text;
using System.Text.Json;
using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Tests.Fixtures;

namespace Garbati.IdentityApi.Tests.Authorization;

// DESIGN.md Section 8, garbati-platform T-020's own "Tests" list: the HTTP-layer checks for the two new
// endpoints (a Reversal operation on the existing POST /v1/authorization/requests, and the new
// .../reversal-outcome). AuthorizationTests.cs already proves identity.authorization.check still gates
// an ordinary Debit/Credit request through this same route - this file is specifically about the
// identity.ops.reversal branch AuthorizationController.RequestAuthorization added.
[Collection("Api")]
public class ReversalAuthorizationHttpTests(ApiFixture fixture)
{
    private static string ReversalRequestBody(string reasonCode = "FRAD") => $$"""
        {"requestedBy":"OPS-ACTOR","operation":"Reversal","debitAccountId":"TXN-HTTP-TEST",
         "amount":1000,"currency":"NGN","idempotencyKey":"rev-http-{{Guid.NewGuid()}}","reasonCode":"{{reasonCode}}"}
        """;

    private static string OutcomeBody() => """{"outcome":"Completed","recordedBy":"ops-user"}""";

    private static HttpRequestMessage PostJson(string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        return request;
    }

    [Fact]
    public async Task RequestAuthorization_ForAReversal_Returns403_WithoutIdentityOpsReversal()
    {
        // Holds every OTHER scope, including identity.authorization.check - proving the Reversal branch
        // really is gated by identity.ops.reversal specifically, not just "any authorization scope."
        using var client = fixture.CreateClient(ApiFixture.AllButClient(IdentityScopes.OpsReversal));

        using var response = await client.SendAsync(PostJson("/v1/authorization/requests", ReversalRequestBody()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RequestAuthorization_ForAReversal_ReachesTheEndpoint_WithOnlyIdentityOpsReversal()
    {
        // Holds ONLY identity.ops.reversal - no identity.authorization.check at all, matching the real
        // dev-ops seed client (appsettings.Development.json) and decision #30's "a single restricted ops
        // caller" shape.
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.OpsReversal));

        using var response = await client.SendAsync(PostJson("/v1/authorization/requests", ReversalRequestBody()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("decision").GetProperty("approved").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("grant").GetString()));
    }

    [Fact]
    public async Task RequestAuthorization_ForAnOrdinaryDebit_StillRequiresIdentityAuthorizationCheck_NotOpsReversal()
    {
        // A caller holding ONLY identity.ops.reversal must NOT be able to use this same endpoint to
        // request an ordinary transfer - that scope was never meant to grant general transfer authority.
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.OpsReversal));
        var debitBody = """
            {"requestedBy":"CUS-DOESNOTEXIST","operation":"Debit","debitAccountId":"ACC-1","creditAccountId":"ACC-2",
             "amount":100,"currency":"NGN","idempotencyKey":"rev-http-debit-leak-test"}
            """;

        using var response = await client.SendAsync(PostJson("/v1/authorization/requests", debitBody));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RequestAuthorization_ForAReversal_Returns400_ForAnUnrecognizedReasonCode()
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.OpsReversal));

        using var response = await client.SendAsync(PostJson("/v1/authorization/requests", ReversalRequestBody(reasonCode: "DISPUTE")));

        // DISPUTE is deliberately not a valid code yet (Section 8.4) - same 400 as any other unrecognized
        // ReasonCode, not a special "almost valid" response.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RecordReversalOutcome_Returns403_WithoutIdentityOpsReversal()
    {
        using var client = fixture.CreateClient(ApiFixture.AllButClient(IdentityScopes.OpsReversal));

        using var response = await client.SendAsync(PostJson("/v1/authorization/requests/DOES-NOT-EXIST/reversal-outcome", OutcomeBody()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RecordReversalOutcome_ReachesTheEndpoint_WithOnlyIdentityOpsReversal()
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.OpsReversal));

        using var response = await client.SendAsync(PostJson("/v1/authorization/requests/DOES-NOT-EXIST/reversal-outcome", OutcomeBody()));

        // Past the scope gate, a nonexistent decision id is a 404 (AuthorizationDecisionNotFoundException),
        // not 401/403 - proves the request actually reached the service.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RecordReversalOutcome_Returns401_WithProblemDetails_WhenNoApiKeyIsSent()
    {
        using var client = fixture.CreateClient(clientId: null);

        using var response = await client.SendAsync(PostJson("/v1/authorization/requests/DOES-NOT-EXIST/reversal-outcome", OutcomeBody()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
