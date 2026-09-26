using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AisEmulator.Api;

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
builder.Services.AddSingleton<BalancerForwarder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BalancerForwarder>());
builder.Services.AddHttpClient(BalancerForwarder.HttpClientName, client =>
{
    client.BaseAddress = new Uri(balancerUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Add("X-Api-Key", balancerKey);
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddProblemDetails();

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

api.MapPut("/executors/{id:long}", (long id, ExecutorBody body, AisStore store, BalancerForwarder forwarder) =>
{
    if (id <= 0 || string.IsNullOrWhiteSpace(body.FullName))
    {
        return Results.Problem(statusCode: 400, title: "Нужны положительный id и ФИО");
    }

    var executor = store.UpsertExecutor(new AisExecutor
    {
        Id = id,
        FullName = body.FullName.Trim(),
        IsActive = body.IsActive,
        DailyLimit = body.DailyLimit,
        QualificationWeight = body.QualificationWeight,
        Attributes = body.Attributes ?? new(),
    });
    forwarder.Enqueue(new ForwardCommand(HttpMethod.Put, $"api/integration/executors/{id}", ToBalancer(executor)));
    return Results.Ok(executor);
});

api.MapPost("/executors/{id:long}/active", (long id, ActiveBody body, AisStore store, BalancerForwarder forwarder) =>
{
    var executor = store.SetExecutorActive(id, body.IsActive);
    if (executor is null)
    {
        return Results.NotFound();
    }

    forwarder.Enqueue(new ForwardCommand(HttpMethod.Put, $"api/integration/executors/{id}", ToBalancer(executor)));
    return Results.Ok(executor);
});

// --- заявки ---
api.MapGet("/orders", (string? status, bool? assigned, int? limit, AisStore store) =>
    Results.Ok(store.Orders(status, assigned, Math.Clamp(limit ?? 100, 1, 1000))));

api.MapGet("/orders/{id:long}", (long id, AisStore store) =>
    store.GetOrder(id) is { } order ? Results.Ok(order) : Results.NotFound());

api.MapPost("/orders", (OrderBody body, AisStore store, BalancerForwarder forwarder) =>
{
    if (body.ParentId is { } parentId && store.GetOrder(parentId) is null)
    {
        return Results.Problem(statusCode: 400, title: "Родительская заявка не найдена");
    }

    var order = store.CreateOrder(body.ParentId, body.Attributes ?? new());
    forwarder.Enqueue(new ForwardCommand(HttpMethod.Post, "api/integration/orders",
        new { id = order.Id, parentId = order.ParentId, attributes = order.Attributes }));
    return Results.Created($"/api/ais/orders/{order.Id}", order);
});

api.MapPost("/orders/{id:long}/status", (long id, StatusBody body, AisStore store, BalancerForwarder forwarder) =>
{
    var status = body.Status?.Trim().ToLowerInvariant();
    if (status is null || !AisStore.Statuses.Contains(status))
    {
        return Results.Problem(statusCode: 400, title: "Статус: processed, await, accept или reject");
    }

    var order = store.SetStatus(id, status);
    if (order is null)
    {
        return Results.NotFound();
    }

    forwarder.Enqueue(new ForwardCommand(HttpMethod.Post, $"api/integration/orders/{id}/status", new { status }));
    return Results.Ok(order);
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

static object ToBalancer(AisExecutor e) => new
{
    fullName = e.FullName,
    isActive = e.IsActive,
    dailyLimit = e.DailyLimit,
    qualificationWeight = e.QualificationWeight,
    attributes = e.Attributes,
};

internal sealed record ExecutorBody(string? FullName, bool IsActive, int? DailyLimit, decimal? QualificationWeight,
    Dictionary<string, JsonElement>? Attributes);

internal sealed record ActiveBody(bool IsActive);

internal sealed record OrderBody(long? ParentId, Dictionary<string, JsonElement>? Attributes);

internal sealed record StatusBody(string? Status);

internal sealed record AssignmentBody(long OrderId, long ExecutorId, long Sequence);

public partial class Program
{
}
