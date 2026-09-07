using FluentValidation;
using Hangfire;
using Masar.Infrastructure.Options;
using Masar.Application.Validators.Auth;
using Masar.Application.Interfaces;
using Masar.Domain.Entities;
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

        // JWT bearer authentication
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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

        app.Run();
    }
}