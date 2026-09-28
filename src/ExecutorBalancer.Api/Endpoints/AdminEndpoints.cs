using System.Text.Json;
using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Application.Executors;
using ExecutorBalancer.Application.Insights;
using ExecutorBalancer.Application.Users;
using ExecutorBalancer.Domain;
using ExecutorBalancer.Infrastructure.Workers;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Api.Endpoints;

public sealed record PreviewRequest(long? ParentId, Dictionary<string, JsonElement>? Attributes);

/// <summary>
/// Конструктор для администратора: отделы, параметры, правила подбора и веса, пробная проверка заявки, журнал.
/// Только после входа; изменяющие запросы — с заголовком защиты от CSRF.
/// </summary>
public static class AdminEndpoints
{
    private static readonly Dictionary<string, JsonElement> NoAttributes = new();

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin")
            .WithTags("Конструктор")
            .RequireAuthorization()
            .AddEndpointFilter<CsrfHeaderFilter>()
            .AddEndpointFilter(TranslateErrors)
            .AddEndpointFilter(DepartmentScope.RequireDepartment);

        group.MapGet("/config", (DepartmentScope d, ConfigurationService config, CancellationToken ct) => config.GetAsync(d.Id, ct));

        group.MapPost("/fields", async (DepartmentScope d, FieldInput input, ConfigurationService config, CancellationToken ct) =>
            Results.Ok(await config.CreateFieldAsync(d.Id, input, ct))).RequireAuthorization(Policies.Admin);
        group.MapPut("/fields/{id:int}", async (DepartmentScope d, int id, FieldInput input, ConfigurationService config, CancellationToken ct) =>
            await config.UpdateFieldAsync(d.Id, id, input, ct) is { } field ? Results.Ok(field) : NotFound()).RequireAuthorization(Policies.Admin);
        group.MapDelete("/fields/{id:int}", async (DepartmentScope d, int id, ConfigurationService config, CancellationToken ct) =>
            await config.DeleteFieldAsync(d.Id, id, ct) ? Results.NoContent() : NotFound()).RequireAuthorization(Policies.Admin);

        group.MapPost("/rules", async (DepartmentScope d, RuleInput input, ConfigurationService config, CancellationToken ct) =>
            Results.Ok(await config.CreateRuleAsync(d.Id, input, ct))).RequireAuthorization(Policies.Admin);
        group.MapPut("/rules/{id:int}", async (DepartmentScope d, int id, RuleInput input, ConfigurationService config, CancellationToken ct) =>
            await config.UpdateRuleAsync(d.Id, id, input, ct) is { } rule ? Results.Ok(rule) : NotFound()).RequireAuthorization(Policies.Admin);
        group.MapDelete("/rules/{id:int}", async (DepartmentScope d, int id, ConfigurationService config, CancellationToken ct) =>
            await config.DeleteRuleAsync(d.Id, id, ct) ? Results.NoContent() : NotFound()).RequireAuthorization(Policies.Admin);

        group.MapPost("/weight-rules", async (DepartmentScope d, WeightRuleInput input, ConfigurationService config, CancellationToken ct) =>
            Results.Ok(await config.CreateWeightRuleAsync(d.Id, input, ct))).RequireAuthorization(Policies.Admin);
        group.MapPut("/weight-rules/{id:int}",
            async (DepartmentScope d, int id, WeightRuleInput input, ConfigurationService config, CancellationToken ct) =>
                await config.UpdateWeightRuleAsync(d.Id, id, input, ct) is { } rule ? Results.Ok(rule) : NotFound()).RequireAuthorization(Policies.Admin);
        group.MapDelete("/weight-rules/{id:int}", async (DepartmentScope d, int id, ConfigurationService config, CancellationToken ct) =>
            await config.DeleteWeightRuleAsync(d.Id, id, ct) ? Results.NoContent() : NotFound()).RequireAuthorization(Policies.Admin);

        // ИИ-разбор заявок: подсказка руководителю, в распределении не участвует
        group.MapGet("/ai/status", (DepartmentScope d, AiAnalyst ai) =>
            Results.Ok(new
            {
                ai.IsConfigured, Model = ai.IsConfigured ? ai.Model : null, Last = ai.Last(d.Id),
                // новый разбор по отделу — не чаще раза в минуту; интерфейс показывает обратный отсчёт
                RetryInSeconds = ai.SecondsUntilNew(d.Id),
            }));
        group.MapPost("/ai/analysis", async (DepartmentScope d, AiAnalyst ai, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await ai.AnalyzeAsync(d.Id, ct));
            }
            catch (AiBusyException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
            }
            catch (AiUnavailableException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "ИИ-разбор недоступен",
                    detail: ex.Message);
            }
        }).RequireAuthorization(Policies.Manager);

        group.MapGet("/presets", (DepartmentScope d, ConfigurationService config, CancellationToken ct) => config.GetPresetsAsync(d.Id, ct));
        group.MapPost("/presets/{id}/apply", async (DepartmentScope d, string id, ConfigurationService config, CancellationToken ct) =>
        {
            await config.ApplyPresetAsync(d.Id, id, ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        // проверка заявки ничего не меняет — доступна и наблюдателю: «почему заявка ушла ему» видят все
        group.MapPost("/preview", Preview);
        group.MapPost("/preview/executors/{id:long}", CheckExecutor);

        // мотивация: рейтинг, режим «больше нормы», защита от работы на количество
        group.MapGet("/motivation", async (DepartmentScope d, ConfigurationService config, CancellationToken ct) =>
            await config.GetMotivationAsync(d.Id, ct) is { } m ? Results.Ok(m) : NotFound());
        group.MapPut("/motivation", async (DepartmentScope d, MotivationInput input, ConfigurationService config,
                CancellationToken ct) =>
            await config.UpdateMotivationAsync(d.Id, input, ct) is { } m ? Results.Ok(m) : NotFound()).RequireAuthorization(Policies.Admin);
        // загрузка сотрудников из файла: шаблон отдела, проверка без записи, запись
        group.MapGet("/executors/template.csv", async (DepartmentScope d, ExecutorImportService import, CancellationToken ct) =>
            Results.File(await import.TemplateAsync(d.Id, ct), "text/csv; charset=utf-8", $"sotrudniki-otdel-{d.Id}.csv")).RequireAuthorization(Policies.Manager);
        group.MapPost("/executors/import", async (DepartmentScope d, bool? apply, HttpRequest request,
            ExecutorImportService import, CancellationToken ct) =>
        {
            var file = await ReadBodyAsync(request, ExecutorImportService.MaxBytes, ct);
            if (file is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Файл слишком большой",
                    detail: $"не больше {ExecutorImportService.MaxBytes / 1024} КБ");
            }

            return Results.Ok(apply == true ? await import.ApplyAsync(d.Id, file, ct) : await import.PreviewAsync(d.Id, file, ct));
        }).RequireAuthorization(Policies.Manager);
        // перерыв, возвращение и увольнение: проверка состава смены — в StaffService; в демо — то же в эмуляторе АИС
        group.MapPost("/executors/{id:long}/active", async (DepartmentScope d, long id, ActiveRequest input, StaffService staff,
            IOptions<DemoOptions> demo, IHttpClientFactory http, IBalancerDbContext db, CancellationToken ct) =>
        {
            if (await staff.SetActiveAsync(d.Id, id, input.IsActive, ct) is null)
            {
                return NotFound();
            }

            if (demo.Value.Enabled)
            {
                await DemoEndpoints.MirrorStaffAsync(http.CreateClient(AisOptions.HttpClientName), db, d.Id, id, input.IsActive, ct);
            }

            return Results.NoContent();
        }).RequireAuthorization(Policies.Manager);
        group.MapDelete("/executors/{id:long}", async (DepartmentScope d, long id, StaffService staff,
            IOptions<DemoOptions> demo, IHttpClientFactory http, IBalancerDbContext db, CancellationToken ct) =>
        {
            if (!await staff.DismissAsync(d.Id, id, ct))
            {
                return NotFound();
            }

            if (demo.Value.Enabled)
            {
                await DemoEndpoints.MirrorStaffAsync(http.CreateClient(AisOptions.HttpClientName), db, d.Id, id, null, ct);
            }

            return Results.NoContent();
        }).RequireAuthorization(Policies.Manager);
        group.MapPut("/executors/{id:long}/extra", async (DepartmentScope d, long id, ExtraModeInput input,
                ConfigurationService config, CancellationToken ct) =>
            await config.SetExtraModeAsync(d.Id, id, input, ct) ? Results.NoContent() : NotFound()).RequireAuthorization(Policies.Manager);

        // отделы: список общий, изменения — только пустых отделов (см. DepartmentService)
        // список — только доступные пользователю отделы
        group.MapGet("/departments", async (HttpContext http, DepartmentService departments, CancellationToken ct) =>
        {
            var access = http.User.Access();
            return (await departments.ListAsync(ct))
                .Where(d => access.CanSee(d.Id) && (!d.IsGuest || access.SeesGuestSandboxes)).ToList();
        });
        group.MapPost("/departments", async (DepartmentInput input, DepartmentService departments, CancellationToken ct) =>
            Results.Ok(await departments.CreateAsync(input, ct))).RequireAuthorization(Policies.Admin);
        group.MapPut("/departments/{id:int}",
            async (int id, DepartmentInput input, DepartmentService departments, CancellationToken ct) =>
                await departments.RenameAsync(id, input, ct) ? Results.NoContent() : NotFound()).RequireAuthorization(Policies.Admin);
        group.MapDelete("/departments/{id:int}", async (int id, DepartmentService departments, CancellationToken ct) =>
            await departments.DeleteAsync(id, ct) ? Results.NoContent() : NotFound()).RequireAuthorization(Policies.Admin);
        // журнал — руководителю и администратору; входы и пользователи (адреса, логины) — только администратору
        group.MapGet("/audit", (DepartmentScope d, long? before, int? limit, HttpContext http, ConfigurationService config,
                CancellationToken ct) =>
            config.GetAuditAsync(d.Id, before, limit ?? 100, ct, security: http.User.Access().Role == UserRole.Admin))
            .RequireAuthorization(Policies.Manager);

        // пользователи и роли — только администратор
        group.MapGet("/users", (UserService users, CancellationToken ct) => users.ListAsync(ct)).RequireAuthorization(Policies.Admin);
        group.MapPost("/users", async (UserInput input, UserService users, CancellationToken ct) =>
            Results.Ok(await users.CreateAsync(input, ct))).RequireAuthorization(Policies.Admin);
        group.MapPut("/users/{id:int}", async (int id, UserInput input, UserService users, CancellationToken ct) =>
            await users.UpdateAsync(id, input, ct) is { } user ? Results.Ok(user) : NotFound()).RequireAuthorization(Policies.Admin);
        group.MapDelete("/users/{id:int}", async (int id, UserService users, CancellationToken ct) =>
            await users.DeleteAsync(id, ct) ? Results.NoContent() : NotFound()).RequireAuthorization(Policies.Admin);
        return app;
    }

    private static async Task<IResult> Preview(DepartmentScope d, PreviewRequest request, OrderBalancer balancer, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.ParentId is <= 0)
        {
            errors["parentId"] = ["должен быть положительным"];
        }

        Contracts.RequestValidation.ValidateAttributes(request.Attributes, errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        return Results.Ok(await balancer.PreviewAsync(d.Id, request.ParentId, request.Attributes ?? NoAttributes, ct));
    }

    /// <summary>Тело запроса целиком, но не больше limit байт; больше — null.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Разбор по правилам для одного сотрудника: почему ему подходит или не подходит такая заявка.</summary>
    private static async Task<IResult> CheckExecutor(DepartmentScope d, long id, PreviewRequest request,
        OrderBalancer balancer, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        Contracts.RequestValidation.ValidateAttributes(request.Attributes, errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        return await balancer.CheckExecutorAsync(d.Id, id, request.Attributes ?? NoAttributes, ct) is { } check
            ? Results.Ok(check)
            : NotFound();
    }

    /// <summary>Ошибки проверки — 400 с полями, конфликты с действующими правилами и данными — 409.</summary>
    private static async ValueTask<object?> TranslateErrors(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidInputException ex)
        {
            return Results.ValidationProblem(ex.Errors);
        }
        catch (ConfigurationConflictException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Изменение не применено",
                detail: ex.Message);
        }
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Запись не найдена");
}
