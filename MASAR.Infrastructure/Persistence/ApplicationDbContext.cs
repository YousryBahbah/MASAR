using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Masar.Domain.Entities;

namespace Masar.Infrastructure.Persistence;

/// <summary>
/// EF Core context. Extends IdentityDbContext so ApplicationUser keeps
/// Identity's default string primary key while every domain entity below
/// uses int, per the locked Step 4 design.
/// </summary>
public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Amenity> Amenities => Set<Amenity>();
    public DbSet<WorkspaceAmenity> WorkspaceAmenities => Set<WorkspaceAmenity>();
    public DbSet<MaintenancePeriod> MaintenancePeriods => Set<MaintenancePeriod>();
    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Must run first — configures AspNetUsers/Roles/Claims/etc.
        // Our IEntityTypeConfiguration classes below run after this and
        // only add to what Identity has already configured.
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
