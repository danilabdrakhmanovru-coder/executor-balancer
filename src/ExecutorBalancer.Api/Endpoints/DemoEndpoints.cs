using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ExecutorBalancer.Api.Contracts;
using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Application.Executors;
using ExecutorBalancer.Application.Insights;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Application.Users;
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

    /// <summary>Через сколько минут без обращений к сайту песочница гостя удаляется.</summary>
    public int GuestResetMinutes { get; set; } = 30;

    /// <summary>Сколько песочниц гостей может быть одновременно — у каждой свой поток в эмуляторе.</summary>
    public int MaxGuests { get; set; } = 15;

    /// <summary>Сфера песочницы гостя — шаблон из DomainPresets.</summary>
    public string GuestPreset { get; set; } = "bank";

    /// <summary>Сколько сотрудников гость может завести в своей песочнице.</summary>
    public int GuestMaxStaff { get; set; } = 50;

    /// <summary>С каким потоком открывается песочница гостя (0 — без потока, гость запустит сам).</summary>
    public double GuestFlowRatePerHour { get; set; } = 1200;

    /// <summary>Поток, запущенный руководителем или гостем: не быстрее стольких заявок в час (4000 — как в кейсе).</summary>
    public double ManagerMaxRatePerHour { get; set; } = 4000;

    /// <summary>…и сам останавливается через столько минут — чтобы забытый поток не шёл сутками.</summary>
    public int ManagerFlowMinutes { get; set; } = 20;
}

/// <param name="Mode">
/// add — добавить столько к имеющимся; exact — сделать в отделе ровно столько (недостающих завести, лишних уволить,
/// остальных не трогать); без режима — заменить набор тестовых сотрудников новым (прежние уходят в неактивные).
/// </param>
public sealed record SeedRequest(int Count, string? Mode = null);

public sealed record SimulationStartRequest(double RatePerHour);

/// <param name="SameQualification">Одинаковая квалификация (по умолчанию); нет — у второго опыт ×2.</param>
public sealed record DuoRequest(bool? SameQualification = null);

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

    /// <summary>Поток для показа на двух сотрудниках: заявка раз в 2–3 секунды — за решениями можно следить глазами.</summary>
    private const double DuoRatePerHour = 1500;

    private static readonly decimal[] Qualifications = [0.8m, 1m, 1m, 1.2m, 1.5m, 2m];

    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        // сотрудники в эмуляторе — у администратора и у гостя в его песочнице; поток и заявки вручную — у руководителя
        var group = DemoGroup(app, Policies.Manager).AddEndpointFilter(AdminOrOwnSandbox);
        var flow = DemoGroup(app, Policies.Manager);

        // «Вернуть демо в исходное» — и руководителю, и гостю с правами руководителя: после гостей всё приводится в порядок
        app.MapPost("/api/admin/demo-reset", async (HttpContext http, CancellationToken ct) =>
            {
                var access = http.User.Access();
                var sandbox = GuestSession.SandboxOf(http.User);
                var (back, added, orders) = await ResetDemoAsync(http.RequestServices, access.CanSee, sandbox, ct);
                return Results.Ok(new { Back = back, Added = added, Orders = orders });
            })
            .WithTags("Демонстрация")
            .RequireAuthorization(Policies.Manager)
            .AddEndpointFilter<CsrfHeaderFilter>()
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.RequestServices.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled
                    ? await next(context)
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Пульт демонстрации выключен"));

        flow.MapGet("/status", async (DepartmentScope d, HttpContext context, IOptions<DemoOptions> options, IBalancerDbContext db,
            IHttpClientFactory http, CancellationToken ct) =>
        {
            // сколько можно этому пользователю: интерфейс ставит такой предел ползунку
            var limits = FlowLimitsFor(context.User, options.Value);
            var code = await CodeAsync(db, d, ct);
            var client = http.CreateClient(AisOptions.HttpClientName);
            var simulation = await Relay(client, HttpMethod.Get, $"api/ais/simulation?department={code}", null, ct);
            if (simulation.Error is not null)
            {
                return simulation.Error;
            }

            var stats = await Relay(client, HttpMethod.Get, $"api/ais/stats?department={code}", null, ct);
            // сколько сотрудников можно завести: гостю — не больше предела песочницы
            var maxStaff = IsAdmin(context) ? MaxSeedCount : Math.Min(MaxSeedCount, options.Value.GuestMaxStaff);
            return stats.Error ?? Results.Ok(new
            {
                Simulation = simulation.Body, Ais = stats.Body, Limits = new { limits.MaxRatePerHour, limits.StopAfterMinutes, MaxStaff = maxStaff },
            });
        });

        // сотрудники отдела в АИС; кого эмулятор не знает (он держит данные в памяти и после перезапуска пуст) —
        // из балансировщика, чтобы их можно было править: правка заведёт их в АИС заново
        group.MapGet("/executors", async (DepartmentScope d, IBalancerDbContext db, IHttpClientFactory http, CancellationToken ct) =>
        {
            var code = await CodeAsync(db, d, ct);
            var inAis = await Relay(http.CreateClient(AisOptions.HttpClientName), HttpMethod.Get,
                $"api/ais/executors?department={code}", null, ct);
            if (inAis.Error is not null)
            {
                return inAis.Error;
            }

            var list = inAis.Body is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToList() : [];
            var known = list.Select(x => x.GetProperty("id").GetInt64()).ToHashSet();
            var missing = await db.Executors.AsNoTracking().Where(x => x.DepartmentId == d.Id).OrderBy(x => x.Id)
                .ToListAsync(ct);
            list.AddRange(missing.Where(x => !known.Contains(x.Id)).Select(x => JsonSerializer.SerializeToElement(
                AisBody(code, x, x.IsActive), JsonSerializerOptions.Web)));
            return Results.Ok(list.OrderBy(x => x.GetProperty("id").GetInt64()));
        });

        group.MapPost("/executors/seed", async (DepartmentScope d, SeedRequest request, HttpContext context,
            IOptions<DemoOptions> options, IBalancerDbContext db, IHttpClientFactory http, ExecutorDirectory directory,
            StaffService staff, CancellationToken ct) =>
        {
            if (request.Count is < 1 or > MaxSeedCount)
            {
                return Invalid("count", $"от 1 до {MaxSeedCount}");
            }

            // гость заводит сотрудников только в своей песочнице и не больше предела
            if (!IsAdmin(context))
            {
                var total = request.Mode == "add"
                    ? await db.Executors.CountAsync(x => x.DepartmentId == d.Id, ct) + request.Count
                    : request.Count;
                if (total > options.Value.GuestMaxStaff)
                {
                    return Invalid("count", $"в гостевом отделе — до {options.Value.GuestMaxStaff} сотрудников");
                }
            }

            var client = http.CreateClient(AisOptions.HttpClientName);
            switch (request.Mode)
            {
                case null or "replace":
                    return (await SeedAsync(d.Id, request.Count, db, client, directory, ct)).Result;
                case "add":
                    return await GrowAsync(d.Id, request.Count, db, client, directory, ct);
                case "exact":
                    await RememberStaffAsync(db, d.Id, request.Count, ct);
                    var staffNow = await db.Executors.AsNoTracking().Where(x => x.DepartmentId == d.Id)
                        .Select(x => new { x.Id, x.IsActive }).ToListAsync(ct);
                    if (staffNow.Count < request.Count)
                    {
                        return await GrowAsync(d.Id, request.Count - staffNow.Count, db, client, directory, ct);
                    }

                    // лишних — увольняем: сначала тех, кто не работает, затем последних заведённых
                    var extra = staffNow.OrderBy(x => x.IsActive).ThenByDescending(x => x.Id)
                        .Take(staffNow.Count - request.Count).Select(x => x.Id).ToList();
                    foreach (var id in extra)
                    {
                        await staff.DismissAsync(d.Id, id, ct, force: true);
                        await MirrorStaffAsync(client, db, d.Id, id, null, ct);
                    }

                    return Results.Ok(new { Added = 0, Removed = extra.Count });
                default:
                    return Invalid("mode", "add, exact или replace");
            }
        });

        // новый сотрудник отдела: заводится в АИС под следующим свободным номером из диапазона отдела
        group.MapPost("/executors", async (DepartmentScope d, ExecutorRequest request, HttpContext context,
            IOptions<DemoOptions> options, IBalancerDbContext db, IHttpClientFactory http, ExecutorDirectory directory,
            CancellationToken ct) =>
        {
            if (!IsAdmin(context)
                && await db.Executors.CountAsync(x => x.DepartmentId == d.Id, ct) >= options.Value.GuestMaxStaff)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Сотрудников достаточно",
                    detail: $"в гостевом отделе — до {options.Value.GuestMaxStaff} сотрудников");
            }

            var errors = RequestValidation.Validate(request);
            if (errors.Count == 0)
            {
                await CheckSkillsAsync(d.Id, request, directory, errors, ct);
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

        group.MapPut("/executors/{id:long}", async (DepartmentScope d, long id, ExecutorRequest request, HttpContext context,
            IBalancerDbContext db, IHttpClientFactory http, ExecutorDirectory directory, CancellationToken ct) =>
        {
            // гость правит только сотрудников своей песочницы: иначе через АИС можно было бы переписать чужого
            if (!IsAdmin(context) && !await db.Executors.AnyAsync(x => x.Id == id && x.DepartmentId == d.Id, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Сотрудник не найден");
            }

            var errors = RequestValidation.Validate(request);
            if (id <= 0)
            {
                errors["id"] = ["должен быть положительным"];
            }

            if (errors.Count == 0)
            {
                await CheckSkillsAsync(d.Id, request, directory, errors, ct);
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

        flow.MapPost("/simulation/start", async (DepartmentScope d, SimulationStartRequest request, HttpContext context,
            IOptions<DemoOptions> options, IBalancerDbContext db, IHttpClientFactory http, ExecutorDirectory directory,
            ICurrentActor actor, TimeProvider clock, CancellationToken ct) =>
        {
            var limits = FlowLimitsFor(context.User, options.Value);
            if (request.RatePerHour is < 1 || request.RatePerHour > limits.MaxRatePerHour || double.IsNaN(request.RatePerHour))
            {
                return Invalid("ratePerHour", $"от 1 до {limits.MaxRatePerHour:0} заявок в час");
            }

            return (await StartFlowAsync(http.CreateClient(AisOptions.HttpClientName), db, directory, actor, clock, d.Id,
                request.RatePerHour, limits.StopAfterMinutes, ct)).Result;
        });

        flow.MapPost("/simulation/stop", async (DepartmentScope d, IBalancerDbContext db, IHttpClientFactory http,
            CancellationToken ct) =>
            (await Relay(http.CreateClient(AisOptions.HttpClientName), HttpMethod.Post,
                $"api/ais/simulation/stop?department={await CodeAsync(db, d, ct)}", new { }, ct)).Result);

        // «Два сотрудника»: отдел с чистого листа, двое одинаковых «умеющих всё» и медленный поток — видно,
        // как заявки делятся между ними. Только в демо; гость — в своей песочнице (как всё заведение сотрудников).
        group.MapPost("/scenario/duo", async (DepartmentScope d, DuoRequest request, HttpContext context,
            IOptions<DemoOptions> options, IBalancerDbContext db, IHttpClientFactory http, ExecutorDirectory directory,
            StaffService staff, ICurrentActor actor, TimeProvider clock, CancellationToken ct) =>
        {
            var client = http.CreateClient(AisOptions.HttpClientName);
            var code = await CodeAsync(db, d, ct);
            await RelayResetAsync(client, code, ct);                   // поток остановлен, заявки АИС забыты
            await staff.ResetDemoAsync(d.Id, automatic: false, ct);    // заявки и статистика — с нуля
            var previous = await db.Executors.AsNoTracking().Where(x => x.DepartmentId == d.Id).Select(x => x.Id).ToListAsync(ct);
            foreach (var id in previous)
            {
                await staff.DismissAsync(d.Id, id, ct, force: true);
                await MirrorStaffAsync(client, db, d.Id, id, null, ct);
            }

            var fields = await db.FieldDefinitions.AsNoTracking().Where(f => f.DepartmentId == d.Id).ToListAsync(ct);
            var rules = await db.Rules.AsNoTracking().Where(r => r.DepartmentId == d.Id).ToListAsync(ct);
            var skills = AllCapable.Skills(fields, rules);
            // номера — после всех, кто когда-либо был в диапазоне отдела: у нового сотрудника своя история квалификации
            var first = (d.Id - 1L) * IdsPerDepartment + 1;
            var last = first + IdsPerDepartment - 1;
            var used = await db.ExecutorQualifications.AsNoTracking().Where(q => q.ExecutorId >= first && q.ExecutorId <= last)
                .Select(q => (long?)q.ExecutorId).MaxAsync(ct) ?? first - 1;
            var ids = new[] { Math.Min(last - 1, used + 1), Math.Min(last, used + 2) };
            var people = new[] { ("Анна С.", 1m), ("Борис К.", request.SameQualification == false ? 2m : 1m) };
            for (var i = 0; i < 2; i++)
            {
                var put = await Relay(client, HttpMethod.Put, $"api/ais/executors/{ids[i]}", new
                {
                    Department = code, FullName = people[i].Item1, IsActive = true, DailyLimit = (int?)null,
                    QualificationWeight = people[i].Item2, Attributes = skills,
                }, ct);
                if (put.Error is not null)
                {
                    return put.Error;
                }
            }

            await RememberStaffAsync(db, d.Id, 2, ct);
            // АИС передаёт сотрудников балансировщику сама — ждём обоих, чтобы первые заявки нашли их
            var until = clock.GetUtcNow().AddSeconds(5);
            while (clock.GetUtcNow() < until && await db.Executors.CountAsync(x => x.DepartmentId == d.Id, ct) < 2)
            {
                await Task.Delay(200, ct);
            }

            var limits = FlowLimitsFor(context.User, options.Value);
            var rate = Math.Min(DuoRatePerHour, limits.MaxRatePerHour);
            var started = await StartFlowAsync(client, db, directory, actor, clock, d.Id, rate, limits.StopAfterMinutes, ct);
            await AuditAsync(db, actor, clock, d.Id, "demo_duo_started",
                new { executors = people.Select(p => p.Item1), sameQualification = request.SameQualification != false }, ct);
            return started.Error ?? Results.Ok(new { Executors = ids, RatePerHour = rate });
        });

        flow.MapPost("/orders", async (DepartmentScope d, DemoOrderRequest request, IBalancerDbContext db,
            IHttpClientFactory http, ExecutorDirectory directory, ICurrentActor actor, TimeProvider clock, CancellationToken ct) =>
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

            var created = await Relay(http.CreateClient(AisOptions.HttpClientName), HttpMethod.Post, "api/ais/orders",
                new
                {
                    Department = await CodeAsync(db, d, ct), request.ParentId, Attributes = request.Attributes ?? new(),
                    NextOrderId = await NextOrderIdAsync(db, ct),
                },
                ct);
            if (created.Error is null && created.Body?.TryGetProperty("id", out var id) == true)
            {
                await AuditAsync(db, actor, clock, d.Id, "demo_order_sent", new { orderId = id.GetInt64() }, ct);
            }

            return created.Result;
        });

        return app;
    }

    /// <summary>Администратор — без ограничений; у руководителя и гостя поток не быстрее кейса и сам останавливается.</summary>
    private static FlowLimits FlowLimitsFor(ClaimsPrincipal user, DemoOptions options) =>
        user.Access().AtLeast(UserRole.Admin)
            ? new FlowLimits(MaxRatePerHour, null)
            : new FlowLimits(Math.Min(MaxRatePerHour, options.ManagerMaxRatePerHour),
                options.ManagerFlowMinutes > 0 ? options.ManagerFlowMinutes : null);

    private sealed record FlowLimits(double MaxRatePerHour, int? StopAfterMinutes);

    private static bool IsAdmin(HttpContext context) => context.User.Access().AtLeast(UserRole.Admin);

    /// <summary>
    /// Сотрудники тестового стенда: администратор — в любом отделе, гость — только в своей песочнице. Адрес без отдела
    /// (например, перерыв напрямую в АИС) — только администратору.
    /// </summary>
    private static async ValueTask<object?> AdminOrOwnSandbox(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var user = context.HttpContext.User;
        if (user.Access().AtLeast(UserRole.Admin)
            || (GuestSession.SandboxOf(user) is { } sandbox
                && context.Arguments.OfType<DepartmentScope>().Any(d => d.Id == sandbox)))
        {
            return await next(context);
        }

        return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "Сотрудников тестового стенда заводит администратор");
    }

    private static RouteGroupBuilder DemoGroup(IEndpointRouteBuilder app, string policy) =>
        app.MapGroup("/api/admin/demo")
            .WithTags("Демонстрация")
            .RequireAuthorization(policy)
            .AddEndpointFilter<CsrfHeaderFilter>()
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.RequestServices.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled
                    ? await next(context)
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Пульт демонстрации выключен"))
            .AddEndpointFilter(DepartmentScope.RequireDepartment);

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
    /// <summary>Поток заявок отдела в эмуляторе АИС; запуск пишется в журнал отдела.</summary>
    internal static async Task<Relayed> StartFlowAsync(HttpClient client, IBalancerDbContext db, ExecutorDirectory directory,
        ICurrentActor actor, TimeProvider clock, int departmentId, double ratePerHour, int? stopAfterMinutes, CancellationToken ct)
    {
        var snapshot = await directory.GetAsync(departmentId, ct);
        var body = new
        {
            Department = await CodeAsync(db, new DepartmentScope(departmentId), ct),
            NextOrderId = await NextOrderIdAsync(db, ct),
            RatePerHour = ratePerHour,
            OrderFields = Specs(snapshot, FieldOwner.Order),
            StopAfterMinutes = stopAfterMinutes,
        };
        var started = await Relay(client, HttpMethod.Post, "api/ais/simulation/start", body, ct);
        if (started.Error is null)
        {
            await AuditAsync(db, actor, clock, departmentId, "demo_flow_started", new { ratePerHour, stopAfterMinutes }, ct);
        }

        return started;
    }

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
            DailyLimits = await DailyLimitsAsync(db, departmentId, ct),
            Qualifications,
        };
        var seeded = await Relay(client, HttpMethod.Post, "api/ais/executors/seed", body, ct);
        if (seeded.Error is null)
        {
            await RememberStaffAsync(db, departmentId, count, ct);
        }

        return seeded;
    }

    /// <summary>Численность, заведённая администратором, — до неё «Вернуть демо в исходное» восстанавливает отдел.</summary>
    private static Task<int> RememberStaffAsync(IBalancerDbContext db, int departmentId, int count, CancellationToken ct) =>
        db.Departments.Where(x => x.Id == departmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DemoStaffCount, count), ct);

    /// <summary>
    /// Вернуть демо в исходное — с чистого листа в каждом отделе, который видно пользователю: поток и заявки в АИС,
    /// заявки и статистика в балансировщике удаляются, все на работу, «больше нормы» выключен, уволенные восполняются.
    /// Песочница гостя — только если это его собственная (ownSandbox). Возвращает, сколько вернулось на работу,
    /// сколько заведено заново и сколько заявок удалено.
    /// </summary>
    internal static async Task<(int Back, int Added, int Orders)> ResetDemoAsync(IServiceProvider services, Func<int, bool> visible,
        int? ownSandbox, CancellationToken ct)
    {
        var db = services.GetRequiredService<IBalancerDbContext>();
        var staff = services.GetRequiredService<StaffService>();
        var directory = services.GetRequiredService<ExecutorDirectory>();
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(AisOptions.HttpClientName);
        var ai = services.GetRequiredService<AiGate>();
        // песочницы гостей сбрасывает только их гость: у администратора кнопка не трогает чужие демонстрации
        var departments = await db.Departments.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.IsGuest })
            .ToListAsync(ct);
        var ids = departments.Where(x => visible(x.Id) && (!x.IsGuest || ownSandbox == x.Id)).Select(x => x.Id);
        int back = 0, added = 0, orders = 0;
        foreach (var id in ids)
        {
            // сначала АИС: поток отдела остановлен и его заявки забыты — новые не придут, пока балансировщик их удаляет
            await RelayResetAsync(client, await CodeAsync(db, new DepartmentScope(id), ct), ct);
            if (await staff.ResetDemoAsync(id, automatic: false, ct) is not { } result)
            {
                continue;
            }

            ai.Forget(id);
            back += result.Back.Count;
            orders += result.Orders;
            foreach (var executor in result.Back)
            {
                await MirrorStaffAsync(client, db, id, executor, true, ct);
            }

            if (result.Missing > 0 && await GrowAsync(id, result.Missing, db, client, directory, ct, remember: false) is IStatusCodeHttpResult { StatusCode: 200 })
            {
                added += result.Missing;
            }
        }

        return (back, added, orders);
    }

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    /// <summary>Поток и заявка вручную — в журнал отдела: так видно, кто и когда нагружал демо.</summary>
    private static async Task AuditAsync(IBalancerDbContext db, ICurrentActor actor, TimeProvider clock, int departmentId,
        string action, object after, CancellationToken ct)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            DepartmentId = departmentId,
            Actor = actor.Name,
            Action = action,
            Entity = "demo",
            EntityId = departmentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataJson = JsonSerializer.Serialize(new { after }, AuditJson),
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }

    internal static async Task RelayResetAsync(HttpClient client, string code, CancellationToken ct)
    {
        if (client.BaseAddress is null)
        {
            return;
        }

        try
        {
            using var response = await client.PostAsJsonAsync($"api/ais/simulation/reset?department={code}", new { }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // эмулятор недоступен — данные балансировщика всё равно сбрасываем
        }
    }

    /// <summary>
    /// Перерыв и увольнение решает балансировщик (<c>StaffService</c>), а эмулятор АИС получает то же, чтобы его данные
    /// не расходились: иначе следующая правка из АИС вернула бы сотрудника. Эмулятор не знает сотрудника (перезапущен) —
    /// заводится заново. Ошибки эмулятора не мешают: решение в балансировщике уже принято.
    /// </summary>
    /// <param name="isActive">Новое состояние; null — сотрудник уволен.</param>
    internal static async Task MirrorStaffAsync(HttpClient client, IBalancerDbContext db, int departmentId, long id,
        bool? isActive, CancellationToken ct)
    {
        if (client.BaseAddress is null)
        {
            return;
        }

        try
        {
            if (isActive is not { } active)
            {
                using var deleted = await client.DeleteAsync($"api/ais/executors/{id}", ct);
                return;
            }

            using var response = await client.PostAsJsonAsync($"api/ais/executors/{id}/active", new ActiveRequest(active), ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                && await db.Executors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) is { } executor)
            {
                var code = await CodeAsync(db, new DepartmentScope(departmentId), ct);
                using var created = await client.PutAsJsonAsync($"api/ais/executors/{id}", AisBody(code, executor, active), ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // эмулятор недоступен — решение уже в балансировщике
        }
    }

    private static object AisBody(string code, Executor e, bool isActive) => new
    {
        e.Id,
        Department = code,
        e.FullName,
        IsActive = isActive,
        e.DailyLimit,
        e.QualificationWeight,
        Attributes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.AttributesJson) ?? [],
    };

    /// <summary>Параметры по справочнику, и заполнены все: без параметра сотрудник берёт любые заявки этого вида.</summary>
    private static async Task CheckSkillsAsync(int departmentId, ExecutorRequest request, ExecutorDirectory directory,
        Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var catalog = (await directory.GetAsync(departmentId, ct)).Catalog;
        var values = catalog.Parse(FieldOwner.Executor, request.Attributes ?? [], errors);
        var missing = catalog.MissingExecutorFields(values);
        if (errors.Count == 0 && missing.Count > 0)
        {
            errors["attributes"] = [FieldCatalog.MissingFieldsMessage(missing)];
        }
    }

    /// <summary>
    /// Добавить сотрудников к имеющимся: номера — следующие свободные в диапазоне отдела (с учётом и балансировщика,
    /// и эмулятора), имена продолжают список, прежние сотрудники не меняются.
    /// </summary>
    /// <param name="remember">Запомнить новую численность как исходную для сброса демо (не при самом сбросе).</param>
    private static async Task<IResult> GrowAsync(int departmentId, int count, IBalancerDbContext db, HttpClient client,
        ExecutorDirectory directory, CancellationToken ct, bool remember = true)
    {
        var code = await CodeAsync(db, new DepartmentScope(departmentId), ct);
        var first = (departmentId - 1L) * IdsPerDepartment + 1;
        var last = first + IdsPerDepartment - 1;
        var known = await Relay(client, HttpMethod.Get, $"api/ais/executors?department={code}", null, ct);
        if (known.Error is not null)
        {
            return known.Error;
        }

        var inAis = known.Body is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Select(x => x.GetProperty("id").GetInt64()).ToList()
            : [];
        var inBalancer = await db.Executors.AsNoTracking().Where(x => x.Id >= first && x.Id <= last)
            .Select(x => x.Id).ToListAsync(ct);
        var next = inAis.Concat(inBalancer).Where(x => x >= first && x <= last).DefaultIfEmpty(first - 1).Max() + 1;
        if (next + count - 1 > last)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "В отделе нет свободных номеров",
                detail: $"для демонстрации — до {IdsPerDepartment} сотрудников в отделе");
        }

        var existing = await db.Executors.AsNoTracking().CountAsync(x => x.DepartmentId == departmentId, ct);
        var snapshot = await directory.GetAsync(departmentId, ct);
        var body = new
        {
            Department = code,
            FirstId = next,
            Count = count,
            Fields = Specs(snapshot, FieldOwner.Executor),
            Names = DomainPresets.ExecutorNames,
            DailyLimits = await DailyLimitsAsync(db, departmentId, ct),
            Qualifications,
            KeepOthers = true,
            NameOffset = existing,
        };
        var created = await Relay(client, HttpMethod.Post, "api/ais/executors/seed", body, ct);
        if (created.Error is not null)
        {
            return created.Error;
        }

        if (remember)
        {
            await RememberStaffAsync(db, departmentId, existing + count, ct);
        }

        return Results.Ok(new { Added = count, Removed = 0 });
    }

    /// <summary>Нормы демо-сотрудников — по сфере отдела (шаблону), чтобы они были похожи на жизнь.</summary>
    private static async Task<int?[]> DailyLimitsAsync(IBalancerDbContext db, int departmentId, CancellationToken ct) =>
        DomainPresets.DailyLimits(await db.Departments.AsNoTracking().Where(x => x.Id == departmentId)
            .Select(x => x.PresetId).FirstOrDefaultAsync(ct));

    /// <summary>Код отдела для АИС. Формат кода проверен при создании отдела — в адрес запроса он попадает как есть.</summary>
    private static Task<string> CodeAsync(IBalancerDbContext db, DepartmentScope d, CancellationToken ct) =>
        db.Departments.AsNoTracking().Where(x => x.Id == d.Id).Select(x => x.Code).FirstAsync(ct);

    /// <summary>С какого номера эмулятору продолжать заявки, чтобы не совпасть с уже принятыми (после его перезапуска).</summary>
    private static async Task<long> NextOrderIdAsync(IBalancerDbContext db, CancellationToken ct) =>
        (await db.Orders.AsNoTracking().MaxAsync(o => (long?)o.Id, ct) ?? 0) + 1;

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
