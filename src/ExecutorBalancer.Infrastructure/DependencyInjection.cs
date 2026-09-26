using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Infrastructure.Persistence;
using ExecutorBalancer.Infrastructure.Redis;
using ExecutorBalancer.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace ExecutorBalancer.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = Required(configuration, "Postgres");
        var redis = Required(configuration, "Redis");

        services.AddDbContext<BalancerDbContext>(options => options
            .UseNpgsql(postgres)
            // миграция написана вручную: при расхождении со снимком модели пишем в лог, а не падаем
            .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning)));
        services.AddScoped<IBalancerDbContext>(sp => sp.GetRequiredService<BalancerDbContext>());

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redis);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });
        services.AddSingleton<ILoadStore, RedisLoadStore>();
        services.AddHostedService<StartupInitializer>();

        services.AddOptions<AisOptions>().Bind(configuration.GetSection(AisOptions.Section));
        services.AddHttpClient(AisOptions.HttpClientName, (sp, client) =>
        {
            var ais = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AisOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(ais.BaseUrl))
            {
                client.BaseAddress = new Uri(ais.BaseUrl.TrimEnd('/') + "/");
            }

            client.DefaultRequestHeaders.Add("X-Api-Key", ais.ApiKey);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<PendingRetryWorker>();
        return services;
    }

    private static string Required(IConfiguration configuration, string name)
    {
        var value = configuration.GetConnectionString(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Не задана строка подключения ConnectionStrings:{name}")
            : value;
    }
}
