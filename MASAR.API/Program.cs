using FluentValidation;
using Hangfire;
using Masar.Infrastructure.Options;
using Masar.Application.Validators.Auth;
using Masar.Application.Interfaces;
using Masar.Domain.Entities;
using Masar.Infrastructure.Jobs;
using Masar.Infrastructure.Persistence;
using Masar.Infrastructure.Persistence.Seed;
using Masar.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;

public partial class Program
{
    private static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Serilog — read config from appsettings (+ Development overrides)
        builder.Host.UseSerilog((context, services, configuration) =>
            configuration.ReadFrom.Configuration(context.Configuration));

        // Add services to the container.
        builder.Services.AddControllers();
        // [ApiController]'s default invalid-ModelState response uses
        // ValidationProblemDetails, a different shape than this API's
        // {code, message} convention. Every other endpoint validates via
        // FluentValidation in the service layer and never hits this path
        // anyway, so suppressing it here only changes behavior for query-
        // bound endpoints like Search that rely on model binding itself
        // failing (a malformed enum, a non-numeric page value).
        builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
        {
            options.SuppressModelStateInvalidFilter = true;
        });

        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();
        builder.Services.AddSwaggerGen();

        // EF Core — ApplicationDbContext (Masar.Infrastructure.Persistence)
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Connection string 'DefaultConnection' not found in configuration.");

        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(connectionString));

        // Identity — ApplicationUser + role support, backed by ApplicationDbContext
        builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            // Tune to taste; defaults are quite strict for a portfolio project.
            options.Password.RequireNonAlphanumeric = false;
        })
            .AddEntityFrameworkStores<ApplicationDbContext>();

        // Jwt settings — bound and validated at startup rather than on first
        // login/token-validation call. A missing Issuer/Audience/SigningKey
        // binds fine as an empty string, so the null-check alone doesn't catch
        // it; a SigningKey too short for HS256 also binds fine and only fails
        // later with a cryptic SecurityTokenInvalidSigningKeyException — both
        // are much worse failure modes than a clear crash here.
        var jwtSettings = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
            ?? throw new InvalidOperationException("Jwt configuration section is missing.");

        if (string.IsNullOrWhiteSpace(jwtSettings.Issuer))
            throw new InvalidOperationException("Jwt:Issuer is missing or empty.");
        if (string.IsNullOrWhiteSpace(jwtSettings.Audience))
            throw new InvalidOperationException("Jwt:Audience is missing or empty.");
        if (string.IsNullOrWhiteSpace(jwtSettings.SigningKey))
            throw new InvalidOperationException(
                "Jwt:SigningKey is missing or empty. Set it via 'dotnet user-secrets set \"Jwt:SigningKey\" \"<value>\"' — do not commit it to appsettings.json.");
        if (Encoding.UTF8.GetByteCount(jwtSettings.SigningKey) < 32)
            throw new InvalidOperationException(
                "Jwt:SigningKey must be at least 32 bytes (256 bits) for HS256.");
        if (jwtSettings.AccessTokenMinutes <= 0)
            throw new InvalidOperationException("Jwt:AccessTokenMinutes must be positive.");

        builder.Services.AddSingleton(jwtSettings);
        builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
        builder.Services.AddScoped<ITokenService, JwtTokenService>();
        builder.Services.AddScoped<IAuthService, AuthService>();
        builder.Services.AddScoped<ILocationService, LocationService>();
        builder.Services.AddScoped<IWorkspaceService, WorkspaceService>();
        builder.Services.AddScoped<IAmenityService, AmenityService>();
        builder.Services.AddScoped<IWorkspaceSearchService, WorkspaceSearchService>();
        builder.Services.AddScoped<IBookingService, BookingService>();
        builder.Services.AddScoped<IAdminService, AdminService>();
        builder.Services.AddScoped<IMaintenancePeriodService, MaintenancePeriodService>();
        builder.Services.AddScoped<BookingLifecycleJobs>();


        //enums converter
        builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        // JWT bearer authentication
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        })
 .AddJwtBearer(options =>
 {
     options.TokenValidationParameters = new TokenValidationParameters
     {
         ValidateIssuer = true,
         ValidIssuer = jwtSettings.Issuer,
         ValidateAudience = true,
         ValidAudience = jwtSettings.Audience,
         ValidateIssuerSigningKey = true,
         IssuerSigningKey = new SymmetricSecurityKey(
             Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
         ValidateLifetime = true,
         ClockSkew = TimeSpan.FromSeconds(30)
     };

     options.Events = new JwtBearerEvents
     {
         OnTokenValidated = async context =>
         {
             var userId = context.Principal?
                 .FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
             var userManager = context.HttpContext.RequestServices
                 .GetRequiredService<UserManager<ApplicationUser>>();
             var user = userId is null ? null : await userManager.FindByIdAsync(userId);
             var tokenSecurityStamp = context.Principal?
                 .FindFirst(JwtTokenService.SecurityStampClaimType)?.Value;

             if (user is null || !user.IsActive ||
                 !string.Equals(tokenSecurityStamp, user.SecurityStamp, StringComparison.Ordinal))
             {
                 context.Fail("This token is no longer valid for the current account state.");
             }
         }
     };
 });

        builder.Services.AddAuthorization();

        // FluentValidation — registered explicitly, not via the deprecated
        // FluentValidation.AspNetCore auto-validation package.
        builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

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
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();

        // Authentication must run before authorization — it's what
        // populates the user principal that UseAuthorization checks.
        app.UseAuthentication();
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

        // Seed roles (Member, WorkspaceManager, Admin) on every startup.
        // Idempotent — RoleSeeder checks RoleExistsAsync first, so this is a
        // no-op after the first run. This is the actual invocation that makes
        // the seeder do anything; the file existing in the solution doesn't
        // run it by itself.
        using (var scope = app.Services.CreateScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            await RoleSeeder.SeedAsync(roleManager);
        }

        // Register recurring booking-lifecycle sweeps via IRecurringJobManager,
        // resolved from DI — NOT the static RecurringJob.AddOrUpdate facade.
        // The static facade only works if something has set the separate
        // static JobStorage.Current global (the old GlobalConfiguration.Configuration
        // pattern); this app configures Hangfire the modern way, through
        // builder.Services.AddHangfire(...), which registers storage into the
        // DI container's JobStorage service but does not also set that static
        // global — so the static facade throws "please call IServiceCollection.AddHangfire...
        // use IRecurringJobManager instead of RecurringJob" at runtime, which is
        // Hangfire correctly identifying the mismatch, not a config bug.
        //
        // AddOrUpdate is itself idempotent (same "safe to run every startup"
        // pattern as RoleSeeder above) and the schedule is persisted in the
        // already-configured MASAR_Jobs SQL Server storage, so this survives
        // an app restart without re-registering anything — restart recovery
        // isn't a new problem this needs to solve, it's confirming
        // infrastructure that's already wired does what it's supposed to.
        //
        // Every 5 minutes, proposed — not a locked business rule. See the
        // Step 13 roadmap.
        using (var scope = app.Services.CreateScope())
        {
            var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

            recurringJobs.AddOrUpdate<BookingLifecycleJobs>(
                "booking-noshow-sweep",
                job => job.SweepNoShowsAsync(),
                "*/5 * * * *");

            recurringJobs.AddOrUpdate<BookingLifecycleJobs>(
                "booking-completion-sweep",
                job => job.SweepCompletionsAsync(),
                "*/5 * * * *");
        }

        app.Run();
    }
}
