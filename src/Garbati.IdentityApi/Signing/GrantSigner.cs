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
// Registered as a singleton: the active private key is parsed once at construction (ECDsa.ImportFromPem
// is not cheap to repeat per request) and JsonWebTokenHandler is safe to reuse across requests.
public sealed class GrantSigner : IGrantSigner, IDisposable
{
    public const string Issuer = "Garbati.IdentityApi";
    public const string Audience = "Garbati.LedgerApi";
    public const string GrantType = "transaction-authorization";

    private readonly ECDsa _ecdsa;
    private readonly SigningCredentials _credentials;
    private readonly string _kid;
    private readonly JsonWebTokenHandler _handler = new();

    public GrantSigner(IOptions<SigningKeyOptions> options)
    {
        var config = options.Value;
        var activeKey = config.Keys.FirstOrDefault(k => k.Kid == config.ActiveKid)
            ?? throw new InvalidOperationException(
                $"SigningKeys:ActiveKid '{config.ActiveKid}' does not match any configured key.");

        _ecdsa = ECDsa.Create();
        _ecdsa.ImportFromPem(activeKey.PrivateKeyPem);
        _kid = activeKey.Kid;

        var key = new ECDsaSecurityKey(_ecdsa) { KeyId = _kid };
        _credentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);
    }

    public SignedGrant Sign(GrantClaims claims)
    {
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
            SigningCredentials = _credentials,
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

    public void Dispose() => _ecdsa.Dispose();
}
