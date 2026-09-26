using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AisEmulator.Api;
using AisEmulator.Api.Simulation;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 256 * 1024;
});

var balancerUrl = builder.Configuration["Balancer:BaseUrl"] ?? "";
var balancerKey = builder.Configuration["Balancer:ApiKey"] ?? "";
var inboundKey = builder.Configuration["Ais:ApiKey"] ?? "";
if (inboundKey.Length < 24 || balancerKey.Length < 24 || string.IsNullOrWhiteSpace(balancerUrl))
{
    throw new InvalidOperationException("Задайте Balancer:BaseUrl, Balancer:ApiKey и Ais:ApiKey (ключи не короче 24 символов)");
}

var minDelay = builder.Configuration.GetValue("Emulator:MinDelaySeconds", 2.0);
var maxDelay = builder.Configuration.GetValue("Emulator:MaxDelaySeconds", 10.0);

builder.Services.AddSingleton<AisStore>();
builder.Services.AddSingleton<AisCommands>();
builder.Services.AddSingleton<BalancerForwarder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BalancerForwarder>());
builder.Services.AddSingleton<SimulationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SimulationService>());
builder.Services.AddHttpClient(BalancerForwarder.HttpClientName, client =>
{
    client.BaseAddress = new Uri(balancerUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Add("X-Api-Key", balancerKey);
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.MaxDepth = 16);

var app = builder.Build();
app.UseExceptionHandler();

var expectedKey = Encoding.UTF8.GetBytes(inboundKey);
var api = app.MapGroup("/api/ais").AddEndpointFilter(async (context, next) =>
{
    var provided = Encoding.UTF8.GetBytes(context.HttpContext.Request.Headers["X-Api-Key"].ToString());
    return CryptographicOperations.FixedTimeEquals(provided, expectedKey)
        ? await next(context)
        : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Неверный ключ");
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- исполнители ---
api.MapGet("/executors", (AisStore store) => Results.Ok(store.Executors()));

api.MapPut("/executors/{id:long}", (long id, ExecutorBody body, AisCommands commands) =>
{
    if (id <= 0 || string.IsNullOrWhiteSpace(body.FullName))
    {
        return Results.Problem(statusCode: 400, title: "Нужны положительный id и ФИО");
    }

    return Results.Ok(commands.UpsertExecutor(new AisExecutor
    {
        Id = id,
        FullName = body.FullName.Trim(),
        IsActive = body.IsActive,
        DailyLimit = body.DailyLimit,
        QualificationWeight = body.QualificationWeight,
        Attributes = body.Attributes ?? new(),
    }));
});

api.MapPost("/executors/{id:long}/active", (long id, ActiveBody body, AisCommands commands) =>
    commands.SetExecutorActive(id, body.IsActive) is { } executor ? Results.Ok(executor) : Results.NotFound());

// --- заявки ---
api.MapGet("/orders", (string? status, bool? assigned, int? limit, AisStore store) =>
    Results.Ok(store.Orders(status, assigned, Math.Clamp(limit ?? 100, 1, 1000))));

api.MapGet("/orders/{id:long}", (long id, AisStore store) =>
    store.GetOrder(id) is { } order ? Results.Ok(order) : Results.NotFound());

api.MapPost("/orders", (OrderBody body, AisStore store, AisCommands commands) =>
{
    if (body.ParentId is { } parentId && store.GetOrder(parentId) is null)
    {
        return Results.Problem(statusCode: 400, title: "Родительская заявка не найдена");
    }

    var order = commands.CreateOrder(body.ParentId, body.Attributes ?? new());
    return Results.Created($"/api/ais/orders/{order.Id}", order);
});

api.MapPost("/orders/{id:long}/status", (long id, StatusBody body, AisCommands commands) =>
{
    var status = body.Status?.Trim().ToLowerInvariant();
    if (status is null || !AisStore.Statuses.Contains(status))
    {
        return Results.Problem(statusCode: 400, title: "Статус: processed, await, accept или reject");
    }

    return commands.SetStatus(id, status) is { } order ? Results.Ok(order) : Results.NotFound();
});

// --- демонстрация: поток заявок и работа исполнителей ---
api.MapGet("/simulation", (SimulationService simulation) => Results.Ok(simulation.Status()));

api.MapPost("/simulation/start", (SimulationRequest body, SimulationService simulation) =>
{
    if (body.Validate() is { } error)
    {
        return Results.Problem(statusCode: 400, title: error);
    }

    simulation.Start(body);
    return Results.Ok(simulation.Status());
});

api.MapPost("/simulation/stop", (SimulationService simulation) =>
{
    simulation.Stop();
    return Results.Ok(simulation.Status());
});

api.MapPost("/executors/seed", (SeedExecutorsRequest body, AisStore store, AisCommands commands) =>
{
    if (body.Validate() is { } error)
    {
        return Results.Problem(statusCode: 400, title: error);
    }

    var created = body.Build(Random.Shared);
    foreach (var executor in created)
    {
        commands.UpsertExecutor(executor);
    }

    // прежние исполнители сверх нового набора уходят в неактивные
    foreach (var extra in store.Executors().Where(e => e.Id > body.Count && e.IsActive))
    {
        commands.SetExecutorActive(extra.Id, false);
    }

    return Results.Ok(created);
});

// --- назначения от балансировщика: запись с задержкой, как в реальной АИС ---
api.MapPost("/assignments", (AssignmentBody body, AisStore store, ILogger<AisStore> logger) =>
{
    if (store.GetOrder(body.OrderId) is null)
    {
        return Results.NotFound();
    }

    var delay = TimeSpan.FromSeconds(minDelay + Random.Shared.NextDouble() * Math.Max(0, maxDelay - minDelay));
    _ = Task.Run(async () =>
    {
        await Task.Delay(delay);
        if (!store.ApplyAssignment(body.OrderId, body.ExecutorId, body.Sequence))
        {
            logger.LogDebug("Назначение {Sequence} для заявки {OrderId} устарело", body.Sequence, body.OrderId);
        }
    });
    return Results.Accepted();
});

api.MapGet("/stats", (AisStore store, BalancerForwarder forwarder) => Results.Ok(new
{
    Store = store.Stats(),
    Forwarding = new { forwarder.Backlog, forwarder.Delivered, forwarder.Dropped },
}));

app.Run();

internal sealed record ExecutorBody(string? FullName, bool IsActive, int? DailyLimit, decimal? QualificationWeight,
    Dictionary<string, JsonElement>? Attributes);

internal sealed record ActiveBody(bool IsActive);

internal sealed record OrderBody(long? ParentId, Dictionary<string, JsonElement>? Attributes);

internal sealed record StatusBody(string? Status);

internal sealed record AssignmentBody(long OrderId, long ExecutorId, long Sequence);

public partial class Program
{
}
