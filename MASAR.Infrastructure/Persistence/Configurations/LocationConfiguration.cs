using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Masar.Domain.Entities;

namespace Masar.Infrastructure.Persistence.Configurations;

public class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Name)
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(l => l.Description)
            .HasMaxLength(500);

        builder.Property(l => l.Address)
            .HasMaxLength(300);

        builder.Property(l => l.CreatedAt)
            .IsRequired();

        // Unique constraint: Location.Name
        builder.HasIndex(l => l.Name)
            .IsUnique();

        // Location 1 -> many Workspaces. Restrict: don't allow deleting a
        // Location that still has Workspaces (protects historical bookings
        // transitively through Workspace's own restrict rule).
        builder.HasMany(l => l.Workspaces)
            .WithOne(w => w.Location)
            .HasForeignKey(w => w.LocationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
