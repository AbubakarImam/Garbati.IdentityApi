namespace Garbati.IdentityApi.Services;

// Generates the internal, human-readable CustomerId (DESIGN.md decision #19/#16) - never a BVN, NIN or
// ledger account number. Same approach as LedgerApi's TransactionReference: ambiguous characters
// (0/O, 1/I) are left out so a CustomerId can be read aloud or typed from a screenshot.
public static class CustomerIdGenerator
{
    private const string AllowedChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int Length = 15;
    private const string Prefix = "CUS";

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
