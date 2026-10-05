using Masar.Domain.Enums;
using Masar.Application.Interfaces;
using Masar.Infrastructure.Persistence;
using Masar.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Masar.IntegrationTests.Support;

// A dedicated, disposable database. Recreated from the real migrations once
// per test run (so the tests also prove the migrations build a working
// schema), then shared by every test class.
//
// NEVER point this at MASAR_DB: recreating it drops the database, and several
// tests run global sweeps. The name guard below makes that mistake fail
// loudly instead of destroying data.
internal static class TestDatabase
{
    private static readonly Lazy<Task> Initialization = new(InitializeAsync);

    public static string ConnectionString => TestConfig.TestConnectionString;

    public static Task EnsureReadyAsync() => Initialization.Value;

    public static ApplicationDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new ApplicationDbContext(options);
    }

    private static async Task InitializeAsync()
    {
        var databaseName = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;
        if (!databaseName.Contains("Test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to recreate database '{databaseName}': the test database name must contain " +
                "'Test' (for example MASAR_TestDb). Fix ConnectionStrings:TestConnection in testsettings.json.");
        }

        await using (var db = NewDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }

        // Same role seeding the API runs at startup.
        await TestServices.InScopeAsync(async services =>
        {
            await RoleSeeder.SeedAsync(services.GetRequiredService<RoleManager<IdentityRole>>());
        });

        // Warm the EF model, connection pool and DI graph so the first
        // "concurrent" test is not secretly serialized behind start-up cost.
        await TestServices.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            await db.Bookings.AnyAsync();
            _ = services.GetRequiredService<IBookingService>();
            _ = services.GetRequiredService<IAdminService>();
        });
    }
}
