using Garbati.IdentityApi.Authorization;

namespace Garbati.IdentityApi.Tests.Fixtures;

// Stands in for the authenticated API client in service tests, which run outside an HTTP request.
public sealed class TestCaller(string? clientId) : ICurrentClient
{
    public string? ClientId { get; } = clientId;

    public static readonly TestCaller Default = new("test-client");
}
