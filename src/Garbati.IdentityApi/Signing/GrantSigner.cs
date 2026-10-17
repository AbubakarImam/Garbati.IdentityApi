using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Garbati.IdentityApi.Signing;

// DESIGN.md Section 3.3/3.5, decisions #4/#6/#26. Signs the transaction-bound authorization grant with
// ES256 using Microsoft.IdentityModel.JsonWebTokens/Tokens's intended APIs (JsonWebTokenHandler,
// SigningCredentials over an ECDsaSecurityKey) - no hand-rolled JWS encoding or ECDSA signing, per the
// task file.
//
// Registered as a singleton, but deliberately holds no shared ECDsa instance (see Sign()'s comment for
// why) - only the configured PEM text and kid, both immutable strings, which are trivially safe to read
// concurrently. JsonWebTokenHandler itself has no per-call mutable state and is the advertised
// reuse-across-threads pattern for that type, so it stays a shared field.
public sealed class GrantSigner : IGrantSigner
{
    public const string Issuer = "Garbati.IdentityApi";
    public const string Audience = "Garbati.LedgerApi";
    public const string GrantType = "transaction-authorization";

    // CacheSignatureProviders defaults to true on CryptoProviderFactory.Default, keyed by (roughly) the
    // security key's Kid + algorithm - NOT by object identity. Sign() builds a fresh ECDsaSecurityKey
    // per call but keeps the same Kid every time (it has to - Kid is the stable, publishable identifier
    // a verifier looks up ahead of time), so the default cache would hand back a stale
    // AsymmetricSignatureProvider still pointing at a PREVIOUS call's already-disposed ECDsa - exactly
    // the kind of subtle, caching-induced bug that makes trusting CryptoProviderFactory's caching to
    // "make concurrent signing safe" the wrong call here, not a safe assumption. Disabling the cache on
    // a dedicated factory (safe to share: with caching off there is no shared mutable cache state left
    // to race on) sidesteps it entirely.
    private static readonly CryptoProviderFactory NoCacheProviderFactory = new() { CacheSignatureProviders = false };

    private readonly string _activeKeyPem;
    private readonly string _kid;
    private readonly JsonWebTokenHandler _handler = new();

    public GrantSigner(IOptions<SigningKeyOptions> options)
    {
        var config = options.Value;
        var activeKey = config.Keys.FirstOrDefault(k => k.Kid == config.ActiveKid)
            ?? throw new InvalidOperationException(
                $"SigningKeys:ActiveKid '{config.ActiveKid}' does not match any configured key.");

        // Fail fast at construction if the configured key can't actually be parsed.
        // SigningKeyOptionsValidator already checks this via ValidateOnStart for the real app; this is
        // defence in depth for anything that constructs GrantSigner directly (e.g. a test).
        using (var probe = ECDsa.Create())
        {
            probe.ImportFromPem(activeKey.PrivateKeyPem);
        }

        _activeKeyPem = activeKey.PrivateKeyPem;
        _kid = activeKey.Kid;
    }

    public SignedGrant Sign(GrantClaims claims)
    {
        // A fresh ECDsa per call, not one long-lived shared instance. .NET's own documentation does not
        // guarantee instance methods of cryptographic types (ECDsa included) are safe for concurrent use
        // from multiple threads, and the underlying platform implementation this wraps - OpenSSL's
        // EC_KEY on Linux/macOS, CNG on Windows - is not documented as safe for concurrent signing
        // either. A shared ECDsa field here, signing concurrent authorization requests from a web
        // server's thread pool, would be exactly the unsafe pattern the BCL warns against. Importing a
        // short-lived EC key from a cached PEM string is cheap (microseconds) next to everything else
        // one authorization request already does (several database round trips) - far cheaper than
        // adding a lock that would serialize every concurrent request through one signing operation.
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(_activeKeyPem);
        var key = new ECDsaSecurityKey(ecdsa) { KeyId = _kid };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256)
        {
            CryptoProviderFactory = NoCacheProviderFactory,
        };

        // Claim list matches the task file's enumeration exactly (DESIGN.md 3.3/3.5): iss, aud, typ, jti,
        // sub, kid, operation, debitAccountId, creditAccountId, amount, currency, idempotencyKey,
        // mandateId, iat, exp. "kid" is included both here (matching 3.5's illustrative body example)
        // and, separately, as the JOSE header parameter a real verifier would actually key off
        // (SigningCredentials.Key.KeyId above) - RFC 8725 Section 3.1 pins algorithm/kid on the
        // verifier's side, never trusting the token to say what to use; that verifier does not exist yet
        // (LedgerApi decision #47 is deferred - DESIGN.md decision #27/D-6).
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = claims.IssuedAt.UtcDateTime,
            NotBefore = claims.IssuedAt.UtcDateTime,
            Expires = claims.ExpiresAt.UtcDateTime,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                ["typ"] = GrantType,
                ["jti"] = claims.Jti,
                ["sub"] = claims.Sub,
                ["kid"] = _kid,
                ["operation"] = claims.Operation,
                ["debitAccountId"] = claims.DebitAccountId,
                ["creditAccountId"] = claims.CreditAccountId,
                ["amount"] = claims.Amount,
                ["currency"] = claims.Currency,
                ["idempotencyKey"] = claims.IdempotencyKey,
                ["mandateId"] = claims.MandateId,
            },
        };

        var token = _handler.CreateToken(descriptor);
        return new SignedGrant(token, _kid, claims.ExpiresAt);
    }
}
