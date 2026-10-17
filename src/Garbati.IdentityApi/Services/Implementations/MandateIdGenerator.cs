namespace Garbati.IdentityApi.Services;

// Same approach as CustomerIdGenerator: an internal, human-readable, generated public identifier - never
// a BVN, NIN or ledger account number. Used as the grant's "mandateId" claim (DESIGN.md Section 3.3/3.5).
public static class MandateIdGenerator
{
    private const string AllowedChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int Length = 15;
    private const string Prefix = "MND";

    public static string Generate()
    {
        var id = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            id[i] = AllowedChars[Random.Shared.Next(AllowedChars.Length)];
        }
        return Prefix + "-" + new string(id);
    }
}
