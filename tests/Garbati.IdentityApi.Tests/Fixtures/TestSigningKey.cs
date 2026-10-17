namespace Garbati.IdentityApi.Tests.Fixtures;

// A dedicated ES256 (P-256) keypair generated only for this test suite - never used anywhere else, never
// a real secret (same "generate and commit openly" approach as appsettings.Development.json's dev
// signing key, kept separate here so tests never depend on that file's contents). PrivateKeyPem
// configures the app under test (ApiFixture); PublicKeyPem lets tests independently verify a grant's
// signature - the "even a simple in-test check" the task file calls for, since no LedgerApi exists yet
// to verify for real.
public static class TestSigningKey
{
    public const string Kid = "identity-test-key-2026-10";

    public const string PrivateKeyPem = """
        -----BEGIN PRIVATE KEY-----
        MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgA2Ul1FrbyMxmfJ9Q
        9sF7eXKXs612u0Nz64TBD2Fg3SehRANCAATUNTMtx6T+8B8Pz786Fcw3V5ZlJQsR
        rxLMqaLNxmc3HfbZblMlL7paFAlGXFCduQyjbDNvw/FO2AvnrouF2WTr
        -----END PRIVATE KEY-----
        """;

    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE1DUzLcek/vAfD8+/OhXMN1eWZSUL
        Ea8SzKmizcZnNx322W5TJS+6WhQJRlxQnbkMo2wzb8PxTtgL566Lhdlk6w==
        -----END PUBLIC KEY-----
        """;
}
