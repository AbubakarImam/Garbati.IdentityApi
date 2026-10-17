using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Garbati.IdentityApi.Signing;

// Bound from the "SigningKeys" configuration section (DESIGN.md Section 4.3.1/4.3.2, decisions #4/#26).
// Keyed by "kid" and shaped as a list from day one, specifically so a second key can be added alongside
// the active one later (planned rotation, 4.3.1) without a breaking config-shape change. This task only
// ever configures and uses one active key - no rotation or emergency-revocation endpoint is built here.
public class SigningKeyOptions
{
    public string ActiveKid { get; set; } = "";
    public List<SigningKeyEntry> Keys { get; set; } = [];
}

public class SigningKeyEntry
{
    public string Kid { get; set; } = "";

    // PKCS8 PEM-encoded EC (P-256) private key. For local dev/test this is committed openly, clearly
    // labeled dev-only - the same transparency T-016 used for its dev API key hashes (README.md). Unlike
    // an API key hash, there is no "hash" equivalent for a signing key: the private key material itself
    // has to be present to sign. Never a real secret - a production deployment supplies its own value
    // through its own configuration/secret store, never this file.
    public string PrivateKeyPem { get; set; } = "";
}

// Runs at startup (ValidateOnStart): an unparsable key or a dangling ActiveKid stops the app from
// booting, instead of surfacing as an unexplained 500 the first time someone calls the authorization
// endpoint (same rationale as ApiKeyOptionsValidator). Unlike ApiKeyOptions (zero clients is a valid,
// if useless, configuration), at least one signing key is required - grant issuance cannot function
// without one, so there is no equivalent "valid but empty" state here.
public class SigningKeyOptionsValidator : IValidateOptions<SigningKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, SigningKeyOptions options)
    {
        var errors = new List<string>();

        if (options.Keys.Count == 0)
        {
            errors.Add("SigningKeys:Keys must contain at least one signing key.");
        }

        var seenKids = new HashSet<string>();
        foreach (var key in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(key.Kid))
            {
                errors.Add("Every SigningKeys:Keys entry needs a non-empty Kid.");
                continue;
            }

            if (!seenKids.Add(key.Kid))
            {
                errors.Add($"Duplicate SigningKeys:Keys Kid '{key.Kid}'.");
            }

            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(key.PrivateKeyPem);
                // ES256 (RFC 7518 / DESIGN.md decision #4) requires the P-256 curve specifically - a
                // valid EC key on a different curve would still parse here but produce a token the
                // algorithm claims to be ES256 while actually signed with the wrong curve.
                if (ecdsa.KeySize != 256)
                {
                    errors.Add($"SigningKeys:Keys '{key.Kid}' is not a P-256 (ES256) key.");
                }
            }
            catch (Exception)
            {
                errors.Add($"SigningKeys:Keys '{key.Kid}' PrivateKeyPem is not a valid EC private key.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.ActiveKid))
        {
            errors.Add("SigningKeys:ActiveKid is required.");
        }
        else if (options.Keys.Count > 0 && options.Keys.All(k => k.Kid != options.ActiveKid))
        {
            errors.Add($"SigningKeys:ActiveKid '{options.ActiveKid}' does not match any configured key.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
