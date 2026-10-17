namespace Garbati.IdentityApi.Signing;

public interface IGrantSigner
{
    SignedGrant Sign(GrantClaims claims);
}
