using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Api.Infrastructure;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Services;
using UrlShortener.Infrastructure.InMemory;
using UrlShortener.Infrastructure.Sqlite;

namespace UrlShortener.Api;

public static class ServiceRegistration
{
    public const string CreateRateLimitPolicy = "create-links";

    public static IServiceCollection AddUrlShortener(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ShortenerOptions.SectionName).Get<ShortenerOptions>() ?? new ShortenerOptions();
        services.AddSingleton(options);
        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICodeGenerator, Base62CodeGenerator>();
        services.AddSingleton<UrlValidator>();
        services.AddScoped<LinkService>();

        AddStorage(services, configuration);
        AddRateLimiting(services, configuration);

        services.AddProblemDetails();
        services.AddOpenApi();
        services.AddHealthChecks();
        return services;
    }

    public static WebApplication UseUrlShortenerPipeline(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRateLimiter();
        if (app.Environment.IsDevelopment())
            app.MapOpenApi();
        return app;
    }

    private static void AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Storage:Provider"] ?? "Sqlite";
        if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ILinkRepository, InMemoryLinkRepository>();
            services.AddSingleton<IClickRepository, InMemoryClickRepository>();
            return;
        }

        var connectionString = configuration["Storage:ConnectionString"] ?? "Data Source=urlshortener.db";
        var database = new SqliteDatabase(connectionString);
        database.EnsureCreated();
        services.AddSingleton(database);
        services.AddSingleton<ILinkRepository, SqliteLinkRepository>();
        services.AddSingleton<IClickRepository, SqliteClickRepository>();
    }

    private static void AddRateLimiting(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("RateLimiting");
        var permitLimit = section.GetValue("PermitLimit", 100);
        var windowSeconds = section.GetValue("WindowSeconds", 60);

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = windowSeconds.ToString();
                return ValueTask.CompletedTask;
            };
            // Partition per client address so one abusive caller cannot exhaust everyone's quota.
            limiter.AddPolicy(CreateRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromSeconds(windowSeconds),
                    QueueLimit = 0,
                }));
        });
    }
}
