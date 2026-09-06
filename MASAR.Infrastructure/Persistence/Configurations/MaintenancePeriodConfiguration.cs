using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Masar.Domain.Entities;

namespace Masar.Infrastructure.Persistence.Configurations;

public class MaintenancePeriodConfiguration : IEntityTypeConfiguration<MaintenancePeriod>
{
    public void Configure(EntityTypeBuilder<MaintenancePeriod> builder)
    {
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Reason)
            .HasMaxLength(500);

        builder.Property(m => m.StartTime)
            .IsRequired();

        builder.Property(m => m.EndTime)
            .IsRequired();

        builder.Property(m => m.CreatedAt)
            .IsRequired();

        // Physical constraint (Step 4, matching Booking's equivalent constraint):
        // StartTime < EndTime. The application should also validate this, but
        // the database enforces the invariant regardless of the code path used
        // to write a row.
        builder.ToTable(t =>
            t.HasCheckConstraint("CK_MaintenancePeriod_StartBeforeEnd", "[StartTime] < [EndTime]"));

        // Index: MaintenancePeriods(WorkspaceId, StartTime, EndTime)
        // Supports the maintenance-conflict check during booking creation.
        builder.HasIndex(m => new { m.WorkspaceId, m.StartTime, m.EndTime });

        // Workspace and CreatedByUser relationships are configured from the
        // Workspace and ApplicationUser sides respectively (both Restrict),
        // to keep each FK's delete behavior defined in exactly one place.
    }
}
