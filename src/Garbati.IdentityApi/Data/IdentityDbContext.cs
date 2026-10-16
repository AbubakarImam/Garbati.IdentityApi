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
    }
}
