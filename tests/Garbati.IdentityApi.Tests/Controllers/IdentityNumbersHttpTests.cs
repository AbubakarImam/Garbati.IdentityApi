using System.Net;
using System.Text;
using System.Text.Json;
using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Tests.Fixtures;

namespace Garbati.IdentityApi.Tests.Controllers;

// T-044 (garbati-platform D-26): HTTP-layer checks for PATCH /v1/customers/{customerId}/identity-numbers
// that AuthorizationTests.cs's scope theory does not cover, plus the one assertion the task file calls
// out specifically - that a real HTTP response's JSON never carries a "bvn"/"nin" key, checked by actually
// parsing a response rather than only relying on CustomerResponse having no such property at compile
// time. Uses the shared Api fixture like CustomerDetailsHttpTests.cs; each test creates its own customer
// with a unique idempotency key, so it does not depend on - or get confused by - any other test's data in
// the same never-reset Postgres instance.
[Collection("Api")]
public class IdentityNumbersHttpTests(ApiFixture fixture)
{
    // Needs both CustomerWrite (to create the customer) and KycManage (to submit identity numbers) -
    // AllButClient gives every scope except the one named, which is neither of those two.
    private HttpClient CreateClientWithBothScopes() => fixture.CreateClient(ApiFixture.AllButClient(IdentityScopes.MandateWrite));

    private static HttpRequestMessage PostJson(string path, string body) =>
        new(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpRequestMessage PatchJson(string path, string body) =>
        new(HttpMethod.Patch, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private async Task<string> CreateCustomerAsync(HttpClient client)
    {
        var key = Guid.NewGuid().ToString();
        using var response = await client.SendAsync(PostJson(
            "/v1/customers", $$"""{"fullName":"Identity Numbers Test","jurisdictionCode":"NG","idempotencyKey":"{{key}}"}"""));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("customerId").GetString()!;
    }

    [Fact]
    public async Task SubmitIdentityNumbers_ResponseJson_HasNoBvnOrNinKeys()
    {
        using var client = CreateClientWithBothScopes();
        var customerId = await CreateCustomerAsync(client);

        using var response = await client.SendAsync(
            PatchJson($"/v1/customers/{customerId}/identity-numbers", """{"bvn":"12345678901","nin":"10987654321"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var keys = body.RootElement.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain("bvn", keys);
        Assert.DoesNotContain("nin", keys);
    }

    [Fact]
    public async Task SubmitIdentityNumbers_Returns404_ForANonexistentCustomer()
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.KycManage));

        using var response = await client.SendAsync(
            PatchJson("/v1/customers/CUS-DOESNOTEXIST/identity-numbers", """{"bvn":"12345678901"}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SubmitIdentityNumbers_Returns400_WhenBothFieldsAreNull()
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.KycManage));

        using var response = await client.SendAsync(
            PatchJson("/v1/customers/CUS-DOESNOTEXIST/identity-numbers", "{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SubmitIdentityNumbers_Returns400_WhenBvnIsNotElevenDigits()
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.KycManage));

        using var response = await client.SendAsync(
            PatchJson("/v1/customers/CUS-DOESNOTEXIST/identity-numbers", """{"bvn":"123"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SubmitIdentityNumbers_Returns200_ThenResubmittingTheSameValue_IsAnIdempotentNoOp()
    {
        using var client = CreateClientWithBothScopes();
        var customerId = await CreateCustomerAsync(client);

        using var first = await client.SendAsync(
            PatchJson($"/v1/customers/{customerId}/identity-numbers", """{"bvn":"12345678901"}"""));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var retry = await client.SendAsync(
            PatchJson($"/v1/customers/{customerId}/identity-numbers", """{"bvn":"12345678901"}"""));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task SubmitIdentityNumbers_Returns409_WhenADifferentValueIsSubmitted_ForAFieldAlreadySet()
    {
        using var client = CreateClientWithBothScopes();
        var customerId = await CreateCustomerAsync(client);

        using var first = await client.SendAsync(
            PatchJson($"/v1/customers/{customerId}/identity-numbers", """{"bvn":"12345678901"}"""));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var conflict = await client.SendAsync(
            PatchJson($"/v1/customers/{customerId}/identity-numbers", """{"bvn":"99999999999"}"""));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType?.MediaType);
    }
}
