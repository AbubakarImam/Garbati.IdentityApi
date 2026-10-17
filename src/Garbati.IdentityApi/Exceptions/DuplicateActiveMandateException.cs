namespace Garbati.IdentityApi.Exceptions;

// Thrown when the database's partial unique index (ix_mandates_customer_account_active) rejects a
// second active mandate for the same (CustomerId, LedgerAccountId) pair (DESIGN.md Section 2.2, decision
// #19). Mapped to 409 - the same "a real constraint, not just application validation" guarantee T-016's
// idempotency work established for customers.
public class DuplicateActiveMandateException : Exception
{
    public DuplicateActiveMandateException()
    {
    }

    public DuplicateActiveMandateException(string message)
        : base(message)
    {
    }

    public DuplicateActiveMandateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
