using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExecutorBalancer.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BalancerOptions>()
            .Bind(configuration.GetSection(BalancerOptions.Section))
            .Validate(o => o.DefaultOrderWeight > 0, "Balancer:DefaultOrderWeight должен быть положительным")
            .Validate(o => TimeZoneInfo.TryFindSystemTimeZoneById(o.TimeZone, out _), "Balancer:TimeZone не найден")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ExecutorDirectory>();
        services.AddSingleton<QualityTracker>();
        services.AddScoped<OrderBalancer>();
        // кто действует — для журнала; интерфейс подменяет на вошедшего пользователя
        services.TryAddScoped<Users.ICurrentActor, Users.SystemActor>();
        services.AddScoped<Users.UserService>();
        services.AddScoped<ConfigurationService>();
        services.AddScoped<DepartmentService>();
        services.AddScoped<Executors.ExecutorImportService>();
        services.AddScoped<AnalyticsService>();
        services.AddScoped<Insights.DemandAnalyzer>();
        services.AddScoped<Insights.AiAnalyst>();
        services.AddSingleton<Insights.AiGate>();
        return services;
    }
}
