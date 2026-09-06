using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Masar.Domain.Entities;

namespace Masar.Infrastructure.Persistence.Configurations;

public class WorkspaceAmenityConfiguration : IEntityTypeConfiguration<WorkspaceAmenity>
{
    public void Configure(EntityTypeBuilder<WorkspaceAmenity> builder)
    {
        // Composite primary key — this is a pure join row, no surrogate Id.
        builder.HasKey(wa => new { wa.WorkspaceId, wa.AmenityId });

        // Lookup index: WorkspaceAmenities(AmenityId)
        // (WorkspaceId is already covered as the leading column of the PK.)
        builder.HasIndex(wa => wa.AmenityId);

        // Cascade in both directions: deleting a Workspace or an Amenity
        // removes the join rows, but never the Workspace/Amenity/Booking
        // rows themselves.
        builder.HasOne(wa => wa.Workspace)
            .WithMany(w => w.WorkspaceAmenities)
            .HasForeignKey(wa => wa.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(wa => wa.Amenity)
            .WithMany(a => a.WorkspaceAmenities)
            .HasForeignKey(wa => wa.AmenityId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
