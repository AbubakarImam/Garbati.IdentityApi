namespace Garbati.IdentityApi.Exceptions;

// T-044: thrown by CustomerService.RecordKycDecisionAsync when granting a KycTier (KycStatus ==
// Verified) and the effective KycTierPolicy's RequiredEvidence names AtLeastOneIdentityNumber or
// BothIdentityNumbers (the only two evidence tokens this task enforces - see the task file's "deliberately
// narrow" scope note) and the customer's CustomerIdentityData does not actually satisfy it. Mapped to 422,
// the same business-rule-failure convention AuthorizationDeniedException/JointApprovalDeniedException
// already use - the request itself is well-formed, it is just not allowed given what is on file.
public class KycTierEvidenceNotMetException : Exception
{
    public KycTierEvidenceNotMetException()
    {
    }

    public KycTierEvidenceNotMetException(string message)
        : base(message)
    {
    }

    public KycTierEvidenceNotMetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
