using System.Text.Json;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Domain;
using ExecutorBalancer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExecutorBalancer.Tests;

/// <summary>Балансировщик на SQLite в памяти и хранилище нагрузки в памяти.</summary>
internal sealed class BalancerFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    private BalancerFixture(SqliteConnection connection, ServiceProvider services, InMemoryLoadStore store)
    {
        _connection = connection;
        _services = services;
        Store = store;
    }

    public InMemoryLoadStore Store { get; }

    /// <summary>Подставная модель для ИИ-разбора: без адреса — «не подключена».</summary>
    public FakeAiChat Ai { get; private init; } = null!;

    public async Task<T> Insights<T>(Func<ExecutorBalancer.Application.Insights.AiAnalyst, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ExecutorBalancer.Application.Insights.AiAnalyst>());
    }

    public async Task<T> Users<T>(Func<ExecutorBalancer.Application.Users.UserService, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ExecutorBalancer.Application.Users.UserService>());
    }

    public async Task<ExecutorBalancer.Application.Insights.DemandReport> Demand(int department = D)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ExecutorBalancer.Application.Insights.DemandAnalyzer>()
            .BuildAsync(department, CancellationToken.None);
    }

    public static async Task<BalancerFixture> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Balancer:TimeZone"] = "UTC",
                ["Balancer:ConfigCheckInterval"] = "00:00:00",
            })
            .Build();

        var store = new InMemoryLoadStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BalancerDbContext>(o => o.UseSqlite(connection));
        services.AddScoped<IBalancerDbContext>(sp => sp.GetRequiredService<BalancerDbContext>());
        services.AddSingleton<ILoadStore>(store);
        services.AddApplication(configuration);
        var ai = new FakeAiChat();
        services.AddSingleton<ExecutorBalancer.Application.Insights.IAiChat>(ai);
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BalancerDbContext>();
            await db.Database.EnsureCreatedAsync();
            await DefaultConfiguration.SeedAsync(db, CancellationToken.None);
        }

        return new BalancerFixture(connection, provider, store) { Ai = ai };
    }

    public async Task<T> Run<T>(Func<OrderBalancer, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<OrderBalancer>());
    }

    public async Task<T> Config<T>(Func<ConfigurationService, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ConfigurationService>());
    }

    public async Task<T> Import<T>(Func<ExecutorBalancer.Application.Executors.ExecutorImportService, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ExecutorBalancer.Application.Executors.ExecutorImportService>());
    }

    public async Task<T> Staff<T>(Func<ExecutorBalancer.Application.Executors.StaffService, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ExecutorBalancer.Application.Executors.StaffService>());
    }

    public async Task<T> Departments<T>(Func<DepartmentService, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<DepartmentService>());
    }

    public async Task<AnalyticsReport> Analytics(AnalyticsPeriod period = AnalyticsPeriod.Today, int department = D)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AnalyticsService>()
            .BuildAsync(department, period, CancellationToken.None);
    }

    /// <summary>Общий (singleton) сервис — например, кэш качества.</summary>
    public T Shared<T>() where T : notnull => _services.GetRequiredService<T>();

    public async Task<T> Query<T>(Func<IBalancerDbContext, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IBalancerDbContext>());
    }

    public Task<BalanceResult> Receive(long id, long? parentId = null, object? attributes = null) =>
        Run(b => b.ReceiveAsync(D, new IncomingOrder(id, parentId, OrderStatus.Processed,
            Attributes(attributes ?? DefaultOrder())), CancellationToken.None));

    public Task<BalanceResult?> ChangeStatus(long id, OrderStatus status) =>
        Run(b => b.ChangeStatusAsync(id, status, CancellationToken.None));

    public Task AddExecutor(long id, decimal qualification = 1m, int? dailyLimit = null, bool active = true,
        string[]? subjects = null, object? extra = null) =>
        Run(async b =>
        {
            await b.UpsertExecutorAsync(D, new IncomingExecutor(id, $"Исполнитель {id}", active, dailyLimit, qualification,
                Merge(Attributes(new
                {
                    min_sum = 0,
                    max_sum = 10_000_000,
                    order_types = new[] { "ORDER_1", "ORDER_2", "ORDER_3" },
                    subjects = subjects ?? new[] { "кредит", "вклад", "карты", "ипотека", "страхование" },
                    segments = new[] { "микро", "малый", "средний", "крупный" },
                    client_classes = new[] { "обычный", "VIP" },
                }), extra)), CancellationToken.None);
            return true;
        });

    public static object DefaultOrder(string subject = "кредит", decimal sum = 50_000, string clientClass = "обычный") =>
        new { sum, order_type = "ORDER_1", subject, client_segment = "малый", client_class = clientClass };

    public static Dictionary<string, JsonElement> Attributes(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static Dictionary<string, JsonElement> Merge(Dictionary<string, JsonElement> values, object? extra)
    {
        if (extra is not null)
        {
            foreach (var (key, value) in Attributes(extra))
            {
                values[key] = value;
            }
        }

        return values;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
