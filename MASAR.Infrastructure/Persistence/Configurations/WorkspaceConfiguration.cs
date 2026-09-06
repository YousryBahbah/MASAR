using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Masar.Domain.Entities;

namespace Masar.Infrastructure.Persistence.Configurations;

public class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.HasKey(w => w.Id);

        builder.Property(w => w.Name)
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(w => w.Type)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(w => w.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(w => w.OpeningTime)
            .HasColumnType("time")
            .IsRequired();

        builder.Property(w => w.ClosingTime)
            .HasColumnType("time")
            .IsRequired();

        builder.Property(w => w.CreatedAt)
            .IsRequired();

        // Physical constraints (Step 4): Capacity > 0, OpeningTime < ClosingTime.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Workspace_Capacity", "[Capacity] > 0");
            t.HasCheckConstraint("CK_Workspace_OpeningBeforeClosing", "[OpeningTime] < [ClosingTime]");
        });

        // Unique constraint: Workspace(LocationId, Name).
        // No separate LocationId-only index: LocationId is already the
        // leading column of this composite index, so SQL Server can use it
        // for LocationId-only lookups without a second index.
        builder.HasIndex(w => new { w.LocationId, w.Name })
            .IsUnique();

        // Workspace 1 -> many Bookings. Restrict: preserves historical
        // bookings — a workspace with any booking history cannot be deleted.
        builder.HasMany(w => w.Bookings)
            .WithOne(b => b.Workspace)
            .HasForeignKey(b => b.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);

        // Workspace 1 -> many MaintenancePeriods. Restrict: preserves
        // historical maintenance records.
        builder.HasMany(w => w.MaintenancePeriods)
            .WithOne(m => m.Workspace)
            .HasForeignKey(m => m.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
