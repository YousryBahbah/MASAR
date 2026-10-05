using Masar.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Masar.IntegrationTests.Api;

// Boots the REAL API (Program.cs, real middleware, real controllers, real
// JWT validation, real Identity) in-process against the dedicated test
// database. Nothing is mocked except where a single test says so explicitly.
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    // Program.cs reads Jwt and connection settings eagerly, before any
    // test-host configuration callback could run, so they are supplied as
    // environment variables — which the default configuration reads first-hand
    // and which override appsettings and user-secrets.
    //
    // Hangfire storage is pointed at the same disposable test database so the
    // API's startup registration of the recurring jobs succeeds. The Hangfire
    // SERVER (the thing that would actually run the 5-minute sweeps) is
    // removed below, so no sweep can fire in the middle of a test and change
    // a booking's status underneath it. Tests that need a sweep call it
    // explicitly (TestData.SweepNoShowsAsync / SweepCompletionsAsync).
    static ApiFactory()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", TestDatabase.ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__HangfireConnection", TestDatabase.ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "integration-tests-only-signing-key-0123456789-abcdef");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": that would load the developer's user-secrets and
        // appsettings.Development.json (including the real local connection
        // string) underneath the overrides above.
        builder.UseEnvironment("Testing");

        // Remove ONLY Hangfire's background server — never every IHostedService.
        // ASP.NET Core's own web-host service (GenericWebHostService) is ALSO
        // registered as an IHostedService; it is what hands the pipeline to the
        // test server. Removing it leaves every request failing with "The server
        // has not been started or no web application was configured".
        //
        // Matching is by the assembly the registration comes from, because
        // Hangfire registers its server through a factory lambda (so
        // ImplementationType is null). If nothing matches, the server simply
        // keeps running — the same behaviour as before this change, which is a
        // harmless failure mode rather than a broken one.
        builder.ConfigureTestServices(services =>
        {
            var hangfireServers = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                     && !descriptor.IsKeyedService
                                     && IsFromHangfire(descriptor))
                .ToList();

            foreach (var descriptor in hangfireServers)
            {
                services.Remove(descriptor);
            }
        });
    }

    private static bool IsFromHangfire(ServiceDescriptor descriptor)
    {
        static bool InHangfireAssembly(Type? type) =>
            type?.Assembly.GetName().Name?.StartsWith("Hangfire", StringComparison.Ordinal) == true;

        return InHangfireAssembly(descriptor.ImplementationType)
            || InHangfireAssembly(descriptor.ImplementationInstance?.GetType())
            || InHangfireAssembly(descriptor.ImplementationFactory?.Method.DeclaringType);
    }
}
