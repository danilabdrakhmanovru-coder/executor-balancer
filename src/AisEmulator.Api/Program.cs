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
builder.Services.AddSingleton<OrderIdSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OrderIdSync>());
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
// ?department=код — только этот отдел; без параметра — вся АИС
api.MapGet("/executors", (string? department, AisStore store) =>
    AisCommands.IsValidDepartment(department) ? Results.Ok(store.Executors(department)) : BadDepartment());

api.MapPut("/executors/{id:long}", (long id, ExecutorBody body, AisCommands commands) =>
{
    if (id <= 0 || string.IsNullOrWhiteSpace(body.FullName))
    {
        return Results.Problem(statusCode: 400, title: "Нужны положительный id и ФИО");
    }

    if (!AisCommands.IsValidDepartment(body.Department))
    {
        return BadDepartment();
    }

    return Results.Ok(commands.UpsertExecutor(new AisExecutor
    {
        Id = id,
        Department = body.Department,
        FullName = body.FullName.Trim(),
        IsActive = body.IsActive,
        DailyLimit = body.DailyLimit,
        QualificationWeight = body.QualificationWeight,
        Attributes = body.Attributes ?? new(),
    }));
});

api.MapPost("/executors/{id:long}/active", (long id, ActiveBody body, AisCommands commands) =>
    commands.SetExecutorActive(id, body.IsActive) is { } executor ? Results.Ok(executor) : Results.NotFound());

// сотрудник уволен в балансировщике — убрать из АИС, чтобы симуляция и правки его не вернули
api.MapDelete("/executors/{id:long}", (long id, AisStore store) =>
    store.RemoveExecutor(id) ? Results.NoContent() : Results.NotFound());

// --- заявки ---
api.MapGet("/orders", (string? status, bool? assigned, int? limit, string? department, AisStore store) =>
    AisCommands.IsValidDepartment(department)
        ? Results.Ok(store.Orders(status, assigned, Math.Clamp(limit ?? 100, 1, 1000), department))
        : BadDepartment());

api.MapGet("/orders/{id:long}", (long id, AisStore store) =>
    store.GetOrder(id) is { } order ? Results.Ok(order) : Results.NotFound());

api.MapPost("/orders", async (OrderBody body, AisStore store, AisCommands commands, OrderIdSync ids, CancellationToken ct) =>
{
    await ids.EnsureAsync(ct); // после перезапуска — продолжить нумерацию балансировщика, а не с 1

    if (!AisCommands.IsValidDepartment(body.Department))
    {
        return BadDepartment();
    }

    if (body.NextOrderId is { } next and > 0)
    {
        store.ContinueOrderIdsFrom(next);
    }

    if (body.ParentId is { } parentId && store.GetOrder(parentId) is null)
    {
        return Results.Problem(statusCode: 400, title: "Родительская заявка не найдена");
    }

    var order = commands.CreateOrder(body.Department, body.ParentId, body.Attributes ?? new());
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

// --- демонстрация: поток заявок и работа исполнителей, у каждого отдела свой ---
api.MapGet("/simulation", (string? department, SimulationService simulation) =>
    AisCommands.IsValidDepartment(department) ? Results.Ok(simulation.Status(department)) : BadDepartment());

api.MapPost("/simulation/start", (SimulationRequest body, SimulationService simulation, AisStore store) =>
{
    if (body.Validate() is null && body.NextOrderId is { } next)
    {
        store.ContinueOrderIdsFrom(next);
    }

    if ((body.Validate() ?? simulation.Start(body)) is { } error)
    {
        return Results.Problem(statusCode: 400, title: error);
    }

    return Results.Ok(simulation.Status(body.Department));
});

api.MapPost("/simulation/stop", (string? department, SimulationService simulation) =>
{
    if (!AisCommands.IsValidDepartment(department))
    {
        return BadDepartment();
    }

    simulation.Stop(department);
    return Results.Ok(simulation.Status(department));
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

    // прежние исполнители отдела вне нового набора уходят в неактивные
    var ids = created.Select(e => e.Id).ToHashSet();
    foreach (var extra in store.Executors(body.Department ?? AisStore.NoDepartment).Where(e => !ids.Contains(e.Id) && e.IsActive))
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

api.MapGet("/stats", (string? department, AisStore store, BalancerForwarder forwarder) =>
    AisCommands.IsValidDepartment(department)
        ? Results.Ok(new
        {
            Store = store.Stats(department),
            Forwarding = new { forwarder.Backlog, forwarder.Delivered, forwarder.Dropped },
        })
        : BadDepartment());

app.Run();

static IResult BadDepartment() =>
    Results.Problem(statusCode: 400, title: "Код отдела: латинские буквы, цифры, «-» и «_», до 32 символов");

internal sealed record ExecutorBody(string? Department, string? FullName, bool IsActive, int? DailyLimit, decimal? QualificationWeight,
    Dictionary<string, JsonElement>? Attributes);

internal sealed record ActiveBody(bool IsActive);

internal sealed record OrderBody(string? Department, long? ParentId, Dictionary<string, JsonElement>? Attributes,
    long? NextOrderId = null);

internal sealed record StatusBody(string? Status);

internal sealed record AssignmentBody(long OrderId, long ExecutorId, long Sequence);

public partial class Program
{
}
