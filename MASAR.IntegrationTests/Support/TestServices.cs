using FluentValidation;
using Masar.Application.DTOs.Bookings;
using Masar.Application.DTOs.Maintenance;
using Masar.Application.Interfaces;
using Masar.Application.Validators.Bookings;
using Masar.Application.Validators.Maintenance;
using Masar.Domain.Entities;
using Masar.Infrastructure.Jobs;
using Masar.Infrastructure.Persistence;
using Masar.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Masar.IntegrationTests.Support;

// The real service classes wired against the real test database — the same
// classes and the same constructor dependencies the API uses. Every call
// should run in its OWN scope: DbContext is not thread-safe, and concurrent
// requests in production each get their own scope as well.
internal static class TestServices
{
    private static readonly Lazy<ServiceProvider> LazyProvider = new(Build);

    public static ServiceProvider Provider => LazyProvider.Value;

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(TestDatabase.ConnectionString));

        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        services.AddScoped<IValidator<CreateBookingRequest>, CreateBookingRequestValidator>();
        services.AddScoped<IValidator<CreateMaintenancePeriodRequest>, CreateMaintenancePeriodRequestValidator>();
        services.AddScoped<IValidator<UpdateMaintenancePeriodRequest>, UpdateMaintenancePeriodRequestValidator>();

        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<IMaintenancePeriodService, MaintenancePeriodService>();
        services.AddScoped<IAdminService, AdminService>();
        services.AddScoped<BookingLifecycleJobs>();

        return services.BuildServiceProvider(validateScopes: true);
    }

    public static async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Provider.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public static async Task InScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = Provider.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }
}
