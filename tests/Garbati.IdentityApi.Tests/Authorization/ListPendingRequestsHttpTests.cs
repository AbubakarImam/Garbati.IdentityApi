using System.Net;
using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Tests.Fixtures;

namespace Garbati.IdentityApi.Tests.Authorization;

// T-031 (garbati-platform): the query-parameter validation on GET /v1/authorization/requests lives only
// in AuthorizationController (status/pageSize parsing, before JointApprovalService is ever called), so
// it needs an HTTP-level test - JointApprovalServiceTests exercises ListPendingRequestsAsync directly and
// never goes through that parsing at all.
//
// Deliberately does NOT assert anything about response content (Items/NextPageToken) - the "Api"
// collection's ApiFixture is one shared Postgres instance for every HTTP test in the assembly (never
// reset between tests, unlike the "Postgres" collection JointApprovalServiceTests uses), so other tests'
// PendingApprovalRequest rows may already exist by the time this runs. Content-level behavior (ordering,
// status filtering, paging, the single-holder exclusion) is covered by
// JointApprovalServiceTests.ListPendingRequestsAsync_* instead, against a fixture that resets per test.
[Collection("Api")]
public class ListPendingRequestsHttpTests(ApiFixture fixture)
{
    [Fact]
    public async Task Returns400_ForAnUnrecognizedStatus()
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.AuthorizationCheck));

        using var response = await client.GetAsync("/v1/authorization/requests?status=NotAStatus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("101")]
    public async Task Returns400_ForAnOutOfRangePageSize(string pageSize)
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.AuthorizationCheck));

        using var response = await client.GetAsync($"/v1/authorization/requests?pageSize={pageSize}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Returns400_ForAMalformedPageToken()
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.AuthorizationCheck));

        using var response = await client.GetAsync("/v1/authorization/requests?pageToken=not-valid-base64!!!");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Returns200_WhenStatusAndPageSizeAreOmitted_DefaultingToPendingAndTheDefaultPageSize()
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.AuthorizationCheck));

        using var response = await client.GetAsync("/v1/authorization/requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Approved")]
    [InlineData("Expired")]
    [InlineData("Cancelled")]
    [InlineData("pending")] // case-insensitive, same as every other enum parse on this controller
    public async Task Returns200_ForEveryRecognizedStatusValue(string status)
    {
        using var client = fixture.CreateClient(ApiFixture.OnlyClient(IdentityScopes.AuthorizationCheck));

        using var response = await client.GetAsync($"/v1/authorization/requests?status={status}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
