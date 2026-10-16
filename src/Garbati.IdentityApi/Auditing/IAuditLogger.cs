using Garbati.IdentityApi.Entities;

namespace Garbati.IdentityApi.Auditing;

public interface IAuditLogger
{
    // Stages an audit row on the current DbContext; it is written by the caller's next SaveChanges,
    // so it commits (or rolls back) together with the change it describes (DESIGN.md Section 6.3).
    AuditEvent Record(AuditEvent entry);
}
