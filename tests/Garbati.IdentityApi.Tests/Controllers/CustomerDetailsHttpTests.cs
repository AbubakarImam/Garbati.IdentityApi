using System.Net;
using System.Text;
using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Tests.Fixtures;

namespace Garbati.IdentityApi.Tests.Controllers;

// T-035 (garbati-platform D-19): HTTP-layer checks for PATCH /v1/customers/{customerId}/details that
// AuthorizationTests.cs's scope theory does not cover - a nonexistent customer actually reaching 404 past
// the scope gate, and the controller's own 400 for an all-null body. Uses the shared Api fixture like
// ReversalAuthorizationHttpTests.cs; its single Postgres instance is never reset between HTTP tests in
// this assembly, so (same as that file) this deliberately checks status codes only, not response content.
[Collection("Api")]
public class CustomerDetailsHttpTests(ApiFixture fixture)
{
    private static HttpRequestMessage PatchJson(string path, string body) =>
        new(HttpMethod.Patch, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task UpdateCustomerDetails_Returns404_ForANonexistentCustomer()
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.CustomerWrite));

        using var response = await client.SendAsync(
            PatchJson("/v1/customers/CUS-DOESNOTEXIST/details", """{"fullName":"Someone New"}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateCustomerDetails_Returns400_WhenEveryFieldIsNull()
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.CustomerWrite));

        using var response = await client.SendAsync(
            PatchJson("/v1/customers/CUS-DOESNOTEXIST/details", "{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
