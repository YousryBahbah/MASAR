using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Masar.Domain.Entities;

namespace Masar.Infrastructure.Persistence.Configurations;

public class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.HasKey(b => b.Id);

        builder.Property(b => b.Status)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(b => b.CheckInMethod)
            .HasConversion<int?>();

        builder.Property(b => b.StartTime)
            .IsRequired();

        builder.Property(b => b.EndTime)
            .IsRequired();

        builder.Property(b => b.CreatedAt)
            .IsRequired();

        // Physical constraint (Step 4): StartTime < EndTime.
        // Cross-midnight is a business rule enforced in the booking use
        // case (Step 2, rule 6), not a DB-level check, since it depends on
        // local operating hours rather than the raw UTC timestamps here.
        builder.ToTable(t =>
            t.HasCheckConstraint("CK_Booking_StartBeforeEnd", "[StartTime] < [EndTime]"));

        // Index: Bookings(WorkspaceId, Status, StartTime, EndTime)
        // The core index — supports the overlap predicate
        // (Existing.StartTime < New.EndTime AND Existing.EndTime > New.StartTime)
        // filtered to Confirmed/CheckedIn statuses during booking creation.
        builder.HasIndex(b => new { b.WorkspaceId, b.Status, b.StartTime, b.EndTime });

        // Index: Bookings(UserId, StartTime)
        // Supports the active-booking-limit check and booking history lookups.
        builder.HasIndex(b => new { b.UserId, b.StartTime });

        // User and Workspace relationships are configured from the
        // ApplicationUser and Workspace sides respectively (both Restrict),
        // to keep each FK's delete behavior defined in exactly one place.
    }
}
