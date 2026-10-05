using Microsoft.Extensions.Configuration;

namespace Masar.IntegrationTests.Support;

internal static class TestConfig
{
    private static readonly IConfigurationRoot Configuration = new ConfigurationBuilder()
        .AddJsonFile("testsettings.json", optional: false)
        // Lets CI or another machine override without editing the file:
        //   ConnectionStrings__TestConnection=Server=...;Database=MASAR_TestDb;...
        .AddEnvironmentVariables()
        .Build();

    public static string TestConnectionString =>
        Configuration.GetConnectionString("TestConnection")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:TestConnection is missing from testsettings.json.");
}
