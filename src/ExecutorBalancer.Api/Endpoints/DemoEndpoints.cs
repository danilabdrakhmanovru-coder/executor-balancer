using System.Net.Http.Json;
using System.Text.Json;
using ExecutorBalancer.Api.Contracts;
using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Domain;
using ExecutorBalancer.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Api.Endpoints;

public sealed class DemoOptions
{
    public const string Section = "Demo";

    /// <summary>Пульт демонстрации: управление эмулятором АИС из интерфейса. В боевом окружении выключен.</summary>
    public bool Enabled { get; set; }
}

public sealed record SeedRequest(int Count);

public sealed record SimulationStartRequest(double RatePerHour);

public sealed record ActiveRequest(bool IsActive);

public sealed record DemoOrderRequest(long? ParentId, Dictionary<string, JsonElement>? Attributes);

/// <summary>
/// Пульт демонстрации. Браузер обращается только к балансировщику, а тот — к эмулятору АИС со своим ключом:
/// ключ АИС на страницу не попадает. Данные исполнителей и заявок проверяются по справочнику параметров
/// до отправки в АИС — иначе АИС приняла бы то, что потом отклонит балансировщик.
/// Всё — в рамках выбранного отдела: АИС получает его код и шлёт заявки и исполнителей в адрес отдела.
/// </summary>
public static class DemoEndpoints
{
    public const int MaxSeedCount = 100; // столько же принимает эмулятор АИС
    public const double MaxRatePerHour = 72_000;
    private const long IdsPerDepartment = 1000;

    private static readonly int?[] DailyLimits = [null, null, 60, 80, 120, 200];
    private static readonly decimal[] Qualifications = [0.8m, 1m, 1m, 1.2m, 1.5m, 2m];

    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/demo")
            .WithTags("Демонстрация")
            .RequireAuthorization()
            .AddEndpointFilter<CsrfHeaderFilter>()
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.RequestServices.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled
                    ? await next(context)
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Пульт демонстрации выключен"))
            .AddEndpointFilter(DepartmentScope.RequireDepartment);

        group.MapGet("/status", async (DepartmentScope d, IBalancerDbContext db, IHttpClientFactory http, CancellationToken ct) =>
        {
            var code = await CodeAsync(db, d, ct);
            var client = http.CreateClient(AisOptions.HttpClientName);
            var simulation = await Relay(client, HttpMethod.Get, $"api/ais/simulation?department={code}", null, ct);
            if (simulation.Error is not null)
            {
                return simulation.Error;
            }

            var stats = await Relay(client, HttpMethod.Get, $"api/ais/stats?department={code}", null, ct);
            return stats.Error ?? Results.Ok(new { Simulation = simulation.Body, Ais = stats.Body });
        });

        group.MapGet("/executors", async (DepartmentScope d, IBalancerDbContext db, IHttpClientFactory http, CancellationToken ct) =>
            (await Relay(http.CreateClient(AisOptions.HttpClientName), HttpMethod.Get,
                $"api/ais/executors?department={await CodeAsync(db, d, ct)}", null, ct)).Result);

        group.MapPost("/executors/seed", async (DepartmentScope d, SeedRequest request, IBalancerDbContext db,
            IHttpClientFactory http, ExecutorDirectory directory, CancellationToken ct) =>
        {
            if (request.Count is < 1 or > MaxSeedCount)
            {
                return Invalid("count", $"от 1 до {MaxSeedCount}");
            }

            return (await SeedAsync(d.Id, request.Count, db, http.CreateClient(AisOptions.HttpClientName), directory, ct)).Result;
        });

        // новый сотрудник отдела: заводится в АИС под следующим свободным номером из диапазона отдела
        group.MapPost("/executors", async (DepartmentScope d, ExecutorRequest request, IBalancerDbContext db,
            IHttpClientFactory http, ExecutorDirectory directory, CancellationToken ct) =>
        {
            var errors = RequestValidation.Validate(request);
            if (errors.Count == 0 && request.Attributes is not null)
            {
                (await directory.GetAsync(d.Id, ct)).Catalog.Parse(FieldOwner.Executor, request.Attributes, errors);
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var client = http.CreateClient(AisOptions.HttpClientName);
            var code = await CodeAsync(db, d, ct);
            var first = (d.Id - 1L) * IdsPerDepartment + 1;
            var last = first + IdsPerDepartment - 1;
            var known = await Relay(client, HttpMethod.Get, $"api/ais/executors?department={code}", null, ct);
            if (known.Error is not null)
            {
                return known.Error;
            }

            var inAis = known.Body is { ValueKind: JsonValueKind.Array } list
                ? list.EnumerateArray().Select(x => x.GetProperty("id").GetInt64())
                : [];
            var inBalancer = await db.Executors.AsNoTracking().Where(x => x.Id >= first && x.Id <= last)
                .Select(x => x.Id).ToListAsync(ct);
            var id = inAis.Concat(inBalancer).Where(x => x >= first && x <= last).DefaultIfEmpty(first - 1).Max() + 1;
            if (id > last)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "В отделе нет свободных номеров",
                    detail: $"для демонстрации — до {IdsPerDepartment} сотрудников в отделе");
            }

            var body = new
            {
                Department = code,
                FullName = request.FullName!.Trim(),
                request.IsActive,
                request.DailyLimit,
                request.QualificationWeight,
                request.Attributes,
            };
            var created = await Relay(client, HttpMethod.Put, $"api/ais/executors/{id}", body, ct);
            return created.Error ?? Results.Ok(new { Id = id });
        });

        group.MapPut("/executors/{id:long}", async (DepartmentScope d, long id, ExecutorRequest request,
            IBalancerDbContext db, IHttpClientFactory http, ExecutorDirectory directory, CancellationToken ct) =>
        {
            var errors = RequestValidation.Validate(request);
            if (id <= 0)
            {
                errors["id"] = ["должен быть положительным"];
            }

            if (errors.Count == 0 && request.Attributes is not null)
            {
                (await directory.GetAsync(d.Id, ct)).Catalog.Parse(FieldOwner.Executor, request.Attributes, errors);
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var body = new
            {
                Department = await CodeAsync(db, d, ct),
                FullName = request.FullName!.Trim(),
                request.IsActive,
                request.DailyLimit,
                request.QualificationWeight,
                request.Attributes,
            };
            return (await Relay(http.CreateClient(AisOptions.HttpClientName), HttpMethod.Put, $"api/ais/executors/{id}",
                body, ct)).Result;
        });

        group.MapPost("/executors/{id:long}/active", async (long id, ActiveRequest request, IHttpClientFactory http,
            CancellationToken ct) =>
            (await Relay(http.CreateClient(AisOptions.HttpClientName), HttpMethod.Post, $"api/ais/executors/{id}/active",
                request, ct)).Result);

        group.MapPost("/simulation/start", async (DepartmentScope d, SimulationStartRequest request, IBalancerDbContext db,
            IHttpClientFactory http, ExecutorDirectory directory, CancellationToken ct) =>
        {
            if (request.RatePerHour is < 1 or > MaxRatePerHour || double.IsNaN(request.RatePerHour))
            {
                return Invalid("ratePerHour", $"от 1 до {MaxRatePerHour} заявок в час");
            }

            var snapshot = await directory.GetAsync(d.Id, ct);
            var body = new
            {
                Department = await CodeAsync(db, d, ct),
                NextOrderId = await NextOrderIdAsync(db, ct),
                request.RatePerHour,
                OrderFields = Specs(snapshot, FieldOwner.Order),
            };
            return (await Relay(http.CreateClient(AisOptions.HttpClientName), HttpMethod.Post, "api/ais/simulation/start", body, ct)).Result;
        });

        group.MapPost("/simulation/stop", async (DepartmentScope d, IBalancerDbContext db, IHttpClientFactory http,
            CancellationToken ct) =>
            (await Relay(http.CreateClient(AisOptions.HttpClientName), HttpMethod.Post,
                $"api/ais/simulation/stop?department={await CodeAsync(db, d, ct)}", new { }, ct)).Result);

        group.MapPost("/orders", async (DepartmentScope d, DemoOrderRequest request, IBalancerDbContext db,
            IHttpClientFactory http, ExecutorDirectory directory, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (request.ParentId is <= 0)
            {
                errors["parentId"] = ["должен быть положительным"];
            }

            RequestValidation.ValidateAttributes(request.Attributes, errors);
            if (errors.Count == 0 && request.Attributes is not null)
            {
                (await directory.GetAsync(d.Id, ct)).Catalog.Parse(FieldOwner.Order, request.Attributes, errors);
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            return (await Relay(http.CreateClient(AisOptions.HttpClientName), HttpMethod.Post, "api/ais/orders",
                new
                {
                    Department = await CodeAsync(db, d, ct), request.ParentId, Attributes = request.Attributes ?? new(),
                    NextOrderId = await NextOrderIdAsync(db, ct),
                },
                ct)).Result;
        });

        return app;
    }

    /// <summary>
    /// Как генерировать значения параметров: по справочнику, с подсказками из шаблонов сфер.
    /// Параметр, заведённый в конструкторе, генератор начинает заполнять сразу.
    /// </summary>
    private static List<object> Specs(BalancerSnapshot snapshot, FieldOwner owner) =>
        snapshot.Catalog.All
            .Where(f => f.Owner == owner)
            .OrderBy(f => f.Id)
            .Select(f =>
            {
                var hint = DomainPresets.Hint(f.Owner, f.Key);
                return (object)new
                {
                    f.Key,
                    Type = f.Type.ToString(),
                    f.Options,
                    hint?.Weights,
                    Min = hint?.Min ?? (f.Type == FieldType.Number ? 0 : null),
                    Max = hint?.Max ?? (f.Type == FieldType.Number ? 100 : null),
                    hint?.Choices,
                    LogScale = hint?.LogScale ?? false,
                    hint?.Probability,
                    MinItems = hint?.MinItems ?? (f.Type == FieldType.Array ? Math.Max(1, (f.Options.Length + 1) / 2) : null),
                    hint?.MaxItems,
                    hint?.Required,
                };
            })
            .ToList();

    internal sealed record Relayed(JsonElement? Body, IResult? Error)
    {
        public IResult Result => Error ?? Results.Ok(Body);
    }

    private static async Task<Relayed> Relay(HttpClient client, HttpMethod method, string path, object? body,
        CancellationToken ct)
    {
        if (client.BaseAddress is null)
        {
            return new(null, Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Адрес АИС не задан (Ais:BaseUrl)"));
        }

        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await client.SendAsync(request, ct);
            var json = response.Content.Headers.ContentLength == 0
                ? (JsonElement?)null
                : await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (response.IsSuccessStatusCode)
            {
                return new(json, null);
            }

            var title = json is { ValueKind: JsonValueKind.Object } problem && problem.TryGetProperty("title", out var t)
                ? t.GetString()
                : null;
            return new(null, Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "АИС отклонила запрос",
                detail: title ?? $"HTTP {(int)response.StatusCode}"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                   && !ct.IsCancellationRequested)
        {
            return new(null, Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "АИС недоступна",
                detail: "Проверьте, что эмулятор АИС запущен"));
        }
    }

    /// <summary>
    /// Завести в АИС сотрудников отдела со случайными навыками по его параметрам: номера — из диапазона отдела,
    /// прежние сотрудники отдела вне набора уходят в неактивные. Используется пультом и заполнением пустых отделов.
    /// </summary>
    internal static async Task<Relayed> SeedAsync(int departmentId, int count, IBalancerDbContext db, HttpClient client,
        ExecutorDirectory directory, CancellationToken ct)
    {
        var snapshot = await directory.GetAsync(departmentId, ct);
        var body = new
        {
            Department = await CodeAsync(db, new DepartmentScope(departmentId), ct),
            // идентификаторы в АИС общие: у каждого отдела свой диапазон, у основного — 1, 2, 3…
            FirstId = (departmentId - 1L) * IdsPerDepartment + 1,
            Count = count,
            Fields = Specs(snapshot, FieldOwner.Executor),
            Names = DomainPresets.ExecutorNames,
            DailyLimits,
            Qualifications,
        };
        return await Relay(client, HttpMethod.Post, "api/ais/executors/seed", body, ct);
    }

    /// <summary>Код отдела для АИС. Формат кода проверен при создании отдела — в адрес запроса он попадает как есть.</summary>
    private static Task<string> CodeAsync(IBalancerDbContext db, DepartmentScope d, CancellationToken ct) =>
        db.Departments.AsNoTracking().Where(x => x.Id == d.Id).Select(x => x.Code).FirstAsync(ct);

    /// <summary>С какого номера эмулятору продолжать заявки, чтобы не совпасть с уже принятыми (после его перезапуска).</summary>
    private static async Task<long> NextOrderIdAsync(IBalancerDbContext db, CancellationToken ct) =>
        (await db.Orders.AsNoTracking().MaxAsync(o => (long?)o.Id, ct) ?? 0) + 1;

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
