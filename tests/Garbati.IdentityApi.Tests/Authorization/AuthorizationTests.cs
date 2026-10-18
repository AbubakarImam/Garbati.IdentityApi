using System.Net;
using System.Text;
using System.Text.Json;
using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Middleware;
using Garbati.IdentityApi.Tests.Fixtures;

namespace Garbati.IdentityApi.Tests.Authorization;

[Collection("Api")]
public class AuthorizationTests(ApiFixture fixture)
{
    private static string CreateCustomerBody() =>
        """{"fullName":"Auth Test","jurisdictionCode":"NG","idempotencyKey":"auth-test-create-customer"}""";
    private static string KycDecisionBody() => """{"kycStatus":"Pending","verificationType":"BVN","provider":"test-provider"}""";
    private static string StatusChangeBody() => """{"reason":"auth test"}""";
    private static string AttachMandateBody() => """{"ledgerAccountId":"ACC-AUTHZ-TEST","operations":["View"]}""";
    private static string AuthorizationRequestBody() => """
        {"requestedBy":"CUS-DOESNOTEXIST","operation":"Debit","debitAccountId":"ACC-1","creditAccountId":"ACC-2",
         "amount":100,"currency":"NGN","idempotencyKey":"auth-scope-test"}
        """;
    private static string ApproveRequestBody() => """{"customerId":"CUS-DOESNOTEXIST"}""";
    private static string ChangeApprovalPolicyBody() => """{"requestedBy":"CUS-DOESNOTEXIST","requiredApprovals":1}""";

    // Every endpoint with the one scope it requires. A non-existent customerId is fine: past
    // authorization the controller/service answers with 404 (or, for authorization requests, 422), not
    // 401/403.
    public static TheoryData<string, string, string?, string> Endpoints => new()
    {
        { "POST", "/v1/customers", CreateCustomerBody(), IdentityScopes.CustomerWrite },
        { "GET", "/v1/customers/CUS-DOESNOTEXIST", null, IdentityScopes.CustomerRead },
        { "PATCH", "/v1/customers/CUS-DOESNOTEXIST/kyc", KycDecisionBody(), IdentityScopes.KycManage },
        { "POST", "/v1/customers/CUS-DOESNOTEXIST/suspend", StatusChangeBody(), IdentityScopes.CustomerWrite },
        { "POST", "/v1/customers/CUS-DOESNOTEXIST/reactivate", StatusChangeBody(), IdentityScopes.CustomerWrite },
        { "POST", "/v1/customers/CUS-DOESNOTEXIST/close", StatusChangeBody(), IdentityScopes.CustomerWrite },
        { "POST", "/v1/customers/CUS-DOESNOTEXIST/mark-deceased", StatusChangeBody(), IdentityScopes.CustomerWrite },
        { "POST", "/v1/customers/CUS-DOESNOTEXIST/mandates", AttachMandateBody(), IdentityScopes.MandateWrite },
        { "GET", "/v1/customers/CUS-DOESNOTEXIST/mandates", null, IdentityScopes.MandateRead },
        { "GET", "/v1/accounts/ACC-DOESNOTEXIST/mandates", null, IdentityScopes.MandateRead },
        { "GET", "/v1/mandates/MND-DOESNOTEXIST", null, IdentityScopes.MandateRead },
        { "POST", "/v1/mandates/MND-DOESNOTEXIST/revoke", StatusChangeBody(), IdentityScopes.MandateWrite },
        { "POST", "/v1/authorization/requests", AuthorizationRequestBody(), IdentityScopes.AuthorizationCheck },
        { "GET", "/v1/authorization/requests/DOES-NOT-EXIST", null, IdentityScopes.AuthorizationCheck },
        { "POST", "/v1/authorization/requests/DOES-NOT-EXIST/approve", ApproveRequestBody(), IdentityScopes.AuthorizationCheck },
        { "GET", "/v1/accounts/ACC-DOESNOTEXIST/approval-policy", null, IdentityScopes.MandateRead },
        { "POST", "/v1/accounts/ACC-DOESNOTEXIST/approval-policy/change-requests", ChangeApprovalPolicyBody(), IdentityScopes.AuthorizationCheck },
    };

    private static HttpRequestMessage Request(string method, string path, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return request;
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Returns401_WithProblemDetails_WhenNoApiKeyIsSent(string method, string path, string? body, string scope)
    {
        _ = scope;
        using var client = fixture.CreateClient(clientId: null);

        using var response = await client.SendAsync(Request(method, path, body));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, response.Headers.WwwAuthenticate.ToString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.Equal(correlationId, problem.RootElement.GetProperty("correlationId").GetString());
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Returns403_WhenTheKeyLacksTheRequiredScope(string method, string path, string? body, string scope)
    {
        using var client = fixture.CreateClient(ApiFixture.AllButClient(scope));

        using var response = await client.SendAsync(Request(method, path, body));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ReachesTheEndpoint_WhenTheKeyHoldsOnlyTheRequiredScope(string method, string path, string? body, string scope)
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(scope));

        using var response = await client.SendAsync(Request(method, path, body));

        // Past authorization the controller answers normally (200 or 404 depending on the data).
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Returns401_ForAnUnknownApiKey()
    {
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, "idk_not_a_real_key");

        using var response = await client.GetAsync("/v1/customers/CUS-DOESNOTEXIST");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
