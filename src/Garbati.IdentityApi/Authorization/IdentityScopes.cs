namespace Garbati.IdentityApi.Authorization;

// Each scope is also the name of the authorization policy that requires it (DESIGN.md Section 5.2).
// T-016 only guards endpoints with CustomerRead, CustomerWrite and KycManage; the rest of the list is
// defined now (per the task file: "fine to define the full scope list now") so T-017 does not need to
// touch this class to add mandate/authorization/freeze endpoints.
public static class IdentityScopes
{
    public const string ClaimType = "scope";

    public const string CustomerRead = "identity.customer.read";
    public const string CustomerWrite = "identity.customer.write";
    public const string KycManage = "identity.kyc.manage";
    public const string MandateRead = "identity.mandate.read";
    public const string MandateWrite = "identity.mandate.write";
    public const string AuthorizationCheck = "identity.authorization.check";
    public const string OpsFreeze = "identity.ops.freeze";

    public static readonly IReadOnlyList<string> All =
        [CustomerRead, CustomerWrite, KycManage, MandateRead, MandateWrite, AuthorizationCheck, OpsFreeze];
}
