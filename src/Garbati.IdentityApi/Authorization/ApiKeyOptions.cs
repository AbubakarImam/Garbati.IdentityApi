using Microsoft.Extensions.Options;

namespace Garbati.IdentityApi.Authorization;

// Bound from the "ApiKeys" configuration section. Only key hashes are stored, never keys.
public class ApiKeyOptions
{
    public List<ApiClient> Clients { get; set; } = [];
}

public class ApiClient
{
    public string ClientId { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
}

// Runs at startup (ValidateOnStart): a typo in a hash or scope stops the app from booting,
// instead of surfacing as unexplained 401/403s in production.
public class ApiKeyOptionsValidator : IValidateOptions<ApiKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiKeyOptions options)
    {
        var errors = new List<string>();

        for (var i = 0; i < options.Clients.Count; i++)
        {
            var client = options.Clients[i];
            var prefix = $"ApiKeys:Clients:{i}";

            if (string.IsNullOrWhiteSpace(client.ClientId))
                errors.Add($"{prefix}: ClientId is required.");
            if (!ApiKeyHasher.TryDecode(client.KeyHash, out _))
                errors.Add($"{prefix} ({client.ClientId}): KeyHash must be a base64-encoded SHA-256 hash.");
            if (client.Scopes.Count == 0)
                errors.Add($"{prefix} ({client.ClientId}): at least one scope is required.");
            foreach (var scope in client.Scopes.Where(s => !IdentityScopes.All.Contains(s)))
                errors.Add($"{prefix} ({client.ClientId}): unknown scope '{scope}'.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
