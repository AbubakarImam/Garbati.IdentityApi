using System.Text.Json;
using Garbati.IdentityApi.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Garbati.IdentityApi.Data;

public class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerIdentityData> CustomerIdentityData => Set<CustomerIdentityData>();
    public DbSet<KycVerification> KycVerifications => Set<KycVerification>();
    public DbSet<KycTierPolicy> KycTierPolicies => Set<KycTierPolicy>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Mandate> Mandates => Set<Mandate>();
    public DbSet<MandatePermission> MandatePermissions => Set<MandatePermission>();
    public DbSet<MandateApprovalPolicy> MandateApprovalPolicies => Set<MandateApprovalPolicy>();
    public DbSet<AuthorizationDecision> AuthorizationDecisions => Set<AuthorizationDecision>();
    public DbSet<PendingApprovalRequest> PendingApprovalRequests => Set<PendingApprovalRequest>();
    public DbSet<ApprovalRecord> ApprovalRecords => Set<ApprovalRecord>();
    public DbSet<ReversalOutcome> ReversalOutcomes => Set<ReversalOutcome>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var stringListConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
        var stringListComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s.GetHashCode())),
            v => v.ToList());

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => c.CustomerId).IsUnique();
            // Keys are scoped to the calling client, same scheme as LedgerApi's transactions/accounts
            // (decision #42/#43), applied here per decision #29. Nulls not distinct: two customers
            // created with no recognizable client (InitiatedBy null) and the same key still collide,
            // the same "unknown client" bucket semantics LedgerApi uses.
            entity.HasIndex(c => new { c.InitiatedBy, c.IdempotencyKey }).IsUnique().AreNullsDistinct(false);
            entity.Property(c => c.CustomerStatus).HasConversion<string>();
            entity.Property(c => c.KycStatus).HasConversion<string>();
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_customers_customer_status", "\"customer_status\" IN ('Active', 'Suspended', 'Deceased', 'Closed')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_customers_kyc_status", "\"kyc_status\" IN ('Pending', 'Verified', 'Rejected', 'Expired')"));

            entity.HasOne(c => c.IdentityData)
                .WithOne(d => d.Customer)
                .HasForeignKey<CustomerIdentityData>(d => d.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerIdentityData>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => d.CustomerId).IsUnique();
        });

        modelBuilder.Entity<KycVerification>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.Property(v => v.Status).HasConversion<string>();
            entity.HasIndex(v => v.CustomerId);
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_kyc_verifications_status", "\"status\" IN ('Pending', 'Verified', 'Rejected', 'Expired')"));

            entity.HasOne(v => v.Customer)
                .WithMany()
                .HasForeignKey(v => v.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Policy shape kept deliberately flexible (decision #11/#24): required evidence and eligible
        // operations are string lists, not fixed columns, since the real values are still pending
        // compliance confirmation (research/nigerian-regulatory-requirements.md).
        modelBuilder.Entity<KycTierPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.JurisdictionCode, p.TierCode, p.EffectiveFrom });
            entity.Property(p => p.RequiredEvidence).HasConversion(stringListConverter).HasColumnType("jsonb")
                .Metadata.SetValueComparer(stringListComparer);
            entity.Property(p => p.EligibleOperations).HasConversion(stringListConverter).HasColumnType("jsonb")
                .Metadata.SetValueComparer(stringListComparer);
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => a.EntityId);
            entity.HasIndex(a => a.CorrelationId);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Status).HasConversion<string>();
            entity.HasIndex(o => o.Status);
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_outbox_messages_status", "\"status\" IN ('Pending', 'Sent', 'Acknowledged', 'Failed')"));
        });

        // DESIGN.md Section 2.2/7.1/7.4, decision #19: the real constraint is the partial unique index
        // below, not application-level validation alone. At most one ACTIVE mandate per (customer,
        // account) - but different customers may each have their own active mandate on the same account
        // (joint accounts, T-018). A plain unique index on (CustomerId, LedgerAccountId) would make that
        // impossible, which is exactly the mistake Section 7.1 calls out by name.
        modelBuilder.Entity<Mandate>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => m.MandateId).IsUnique();
            entity.HasIndex(m => new { m.CustomerId, m.LedgerAccountId })
                .IsUnique()
                .HasDatabaseName("ix_mandates_customer_account_active")
                .HasFilter("\"status\" = 'Active'");
            entity.HasIndex(m => m.LedgerAccountId);
            entity.Property(m => m.Status).HasConversion<string>();
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_mandates_status", "\"status\" IN ('Active', 'Revoked')"));

            entity.HasOne(m => m.Customer)
                .WithMany()
                .HasForeignKey(m => m.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MandatePermission>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.MandateId, p.Operation }).IsUnique();
            entity.Property(p => p.Operation).HasConversion<string>();
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_mandate_permissions_operation", "\"operation\" IN ('View', 'Debit', 'Credit')"));

            entity.HasOne(p => p.Mandate)
                .WithMany(m => m.Permissions)
                .HasForeignKey(p => p.MandateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DESIGN.md Section 2.2a/7.1, decision #20: one row per LedgerAccountId, created alongside that
        // account's first mandate (MandateService.AttachMandateAsync). RequiredApprovals defaults to 1 -
        // T-017 never writes anything else (see the entity's own comment).
        modelBuilder.Entity<MandateApprovalPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.LedgerAccountId).IsUnique();
        });

        modelBuilder.Entity<AuthorizationDecision>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => d.DecisionId).IsUnique();
        });

        // DESIGN.md Section 3.8.2/7.1, T-018. RequestId is the public, nonce-equivalent identifier every
        // approval is bound to (decision #25). LedgerAccountId is indexed, not unique - many requests
        // (over time, or ThresholdChange alongside Debit) can exist for the same account.
        modelBuilder.Entity<PendingApprovalRequest>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => r.RequestId).IsUnique();
            entity.HasIndex(r => r.LedgerAccountId);
            entity.Property(r => r.Kind).HasConversion<string>();
            entity.Property(r => r.Status).HasConversion<string>();
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_pending_approval_requests_kind", "\"kind\" IN ('Debit', 'ThresholdChange')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_pending_approval_requests_status", "\"status\" IN ('Pending', 'Approved', 'Expired', 'Cancelled')"));
        });

        // DESIGN.md Section 3.8.2/7.4: the real constraint, not just application validation - a holder
        // cannot approve the same request twice, and this is also how the requester's own implicit
        // approval is represented (one row, created at the same time as the request).
        modelBuilder.Entity<ApprovalRecord>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.PendingApprovalRequestId, a.CustomerId })
                .IsUnique()
                .HasDatabaseName("ix_approval_records_request_customer");

            entity.HasOne(a => a.PendingApprovalRequest)
                .WithMany(r => r.Approvals)
                .HasForeignKey(a => a.PendingApprovalRequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DESIGN.md Section 8.6/7.1, decision #34, T-020. AuthorizationDecisionId references
        // AuthorizationDecision's own DecisionId (a string, not its internal numeric Id) - both tables
        // live in the same database, so this is a real, enforced foreign key (Section 7.4), not just an
        // opaque cross-service string the way LedgerAccountId is. The unique index is the actual "at most
        // one outcome per decision" guard (decision #34: "a real DB constraint, not just app code"),
        // mirroring decision #19/#25's established precedent elsewhere in this schema.
        modelBuilder.Entity<ReversalOutcome>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.HasIndex(o => o.AuthorizationDecisionId).IsUnique();
            entity.Property(o => o.Outcome).HasConversion<string>();
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_reversal_outcomes_outcome", "\"outcome\" IN ('Completed', 'WriteOff')"));

            entity.HasOne<AuthorizationDecision>()
                .WithMany()
                .HasForeignKey(o => o.AuthorizationDecisionId)
                .HasPrincipalKey(d => d.DecisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
