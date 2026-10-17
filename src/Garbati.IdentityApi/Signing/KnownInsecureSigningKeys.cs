using System.Security.Cryptography;

namespace Garbati.IdentityApi.Signing;

// SHA-256 fingerprints of the PUBLIC key material of every signing key this repository hands out
// openly for non-production use: the dev key committed in appsettings.Development.json, and the
// test-only key in the test project's Fixtures/TestSigningKey.cs. SigningKeyOptionsValidator uses these
// to refuse starting in the Production environment with either key configured - the owner's explicit
// condition for allowing #TBD-15's dev/test private key material to be committed openly in the first
// place (see docs/DESIGN.md Appendix C).
//
// Fingerprinting the exported PUBLIC key (ECDsa.ExportSubjectPublicKeyInfo()), not comparing the
// configured "kid" string and not hashing the raw PrivateKeyPem text, is the robust choice on both
// counts:
//   - Comparing "kid" would miss the same key material relabeled under a different kid - kid is just a
//     string a deployer controls, not a property of the key itself.
//   - Hashing the raw PEM text would miss the same key re-pasted with different line endings or
//     wrapping - cosmetically different text, cryptographically the same key. Comparing the parsed
//     public key's canonical DER bytes is invariant to all of that.
// Storing only a fingerprint (a one-way hash), never the private key material itself, mirrors
// ApiKeyOptions storing only a hash of each API key - having these constants here does not let anyone
// recover a key from them.
public static class KnownInsecureSigningKeys
{
    // appsettings.Development.json's "identity-dev-key-2026-10" entry.
    public const string DevKeyFingerprint = "ziUcRAhNGutNXJPxW+4KMClZoMzZprdhQF3M71v0wds=";

    // tests/Garbati.IdentityApi.Tests/Fixtures/TestSigningKey.cs.
    public const string TestKeyFingerprint = "6iH1/t6x43SNiAyem5TrqJjt0DuYVUacFEp8QK4vGyE=";

    public static readonly IReadOnlyCollection<string> Fingerprints = [DevKeyFingerprint, TestKeyFingerprint];

    public static string Fingerprint(ECDsa ecdsa) =>
        Convert.ToBase64String(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo()));
}
