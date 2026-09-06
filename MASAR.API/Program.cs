using Hangfire;
using Hangfire.Dashboard;
using Masar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Serilog;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Serilog — read config from appsettings (+ Development overrides)
        builder.Host.UseSerilog((context, services, configuration) =>
            configuration.ReadFrom.Configuration(context.Configuration));

        // Add services to the container.
        builder.Services.AddControllers();

        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        // EF Core — ApplicationDbContext (Masar.Infrastructure.Persistence)
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Connection string 'DefaultConnection' not found in configuration.");

        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(connectionString));

        // Hangfire
        var hangfireConnectionString = builder.Configuration.GetConnectionString("HangfireConnection")
            ?? throw new InvalidOperationException(
                "Connection string 'HangfireConnection' not found in configuration.");

        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(hangfireConnectionString));

        builder.Services.AddHangfireServer();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();
        app.UseAuthorization();

    //    _ = app.UseHangfireDashboard("/hangfire", new DashboardOptions
    //    {
    //        Authorization = new[]
    //        {
    //    new BasicAuthAuthorizationFilter(new BasicAuthAuthorizationFilterOptions
    //    {
    //        RequireSsl = false,
    //        SslRedirect = false,
    //        LoginCaseSensitive = true,
    //        Users = new[]
    //        {
    //            new BasicAuthAuthorizationUser
    //            {
    //                Login = builder.Configuration["HangfireSettings:Username"]!,
    //                PasswordClear = builder.Configuration["HangfireSettings:Password"]!
    //            }
    //        }
    //    })
    //}
    //    });

        app.MapControllers();

        app.Run();
    }
}