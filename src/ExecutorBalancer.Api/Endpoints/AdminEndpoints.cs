using System.Text.Json;
using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Application.Executors;

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
            Results.Ok(await config.CreateFieldAsync(d.Id, input, ct)));
        group.MapPut("/fields/{id:int}", async (DepartmentScope d, int id, FieldInput input, ConfigurationService config, CancellationToken ct) =>
            await config.UpdateFieldAsync(d.Id, id, input, ct) is { } field ? Results.Ok(field) : NotFound());
        group.MapDelete("/fields/{id:int}", async (DepartmentScope d, int id, ConfigurationService config, CancellationToken ct) =>
            await config.DeleteFieldAsync(d.Id, id, ct) ? Results.NoContent() : NotFound());

        group.MapPost("/rules", async (DepartmentScope d, RuleInput input, ConfigurationService config, CancellationToken ct) =>
            Results.Ok(await config.CreateRuleAsync(d.Id, input, ct)));
        group.MapPut("/rules/{id:int}", async (DepartmentScope d, int id, RuleInput input, ConfigurationService config, CancellationToken ct) =>
            await config.UpdateRuleAsync(d.Id, id, input, ct) is { } rule ? Results.Ok(rule) : NotFound());
        group.MapDelete("/rules/{id:int}", async (DepartmentScope d, int id, ConfigurationService config, CancellationToken ct) =>
            await config.DeleteRuleAsync(d.Id, id, ct) ? Results.NoContent() : NotFound());

        group.MapPost("/weight-rules", async (DepartmentScope d, WeightRuleInput input, ConfigurationService config, CancellationToken ct) =>
            Results.Ok(await config.CreateWeightRuleAsync(d.Id, input, ct)));
        group.MapPut("/weight-rules/{id:int}",
            async (DepartmentScope d, int id, WeightRuleInput input, ConfigurationService config, CancellationToken ct) =>
                await config.UpdateWeightRuleAsync(d.Id, id, input, ct) is { } rule ? Results.Ok(rule) : NotFound());
        group.MapDelete("/weight-rules/{id:int}", async (DepartmentScope d, int id, ConfigurationService config, CancellationToken ct) =>
            await config.DeleteWeightRuleAsync(d.Id, id, ct) ? Results.NoContent() : NotFound());

        group.MapGet("/presets", (DepartmentScope d, ConfigurationService config, CancellationToken ct) => config.GetPresetsAsync(d.Id, ct));
        group.MapPost("/presets/{id}/apply", async (DepartmentScope d, string id, ConfigurationService config, CancellationToken ct) =>
        {
            await config.ApplyPresetAsync(d.Id, id, ct);
            return Results.NoContent();
        });

        group.MapPost("/preview", Preview);
        group.MapPost("/preview/executors/{id:long}", CheckExecutor);

        // мотивация: рейтинг, режим «больше нормы», защита от работы на количество
        group.MapGet("/motivation", async (DepartmentScope d, ConfigurationService config, CancellationToken ct) =>
            await config.GetMotivationAsync(d.Id, ct) is { } m ? Results.Ok(m) : NotFound());
        group.MapPut("/motivation", async (DepartmentScope d, MotivationInput input, ConfigurationService config,
                CancellationToken ct) =>
            await config.UpdateMotivationAsync(d.Id, input, ct) is { } m ? Results.Ok(m) : NotFound());
        // загрузка сотрудников из файла: шаблон отдела, проверка без записи, запись
        group.MapGet("/executors/template.csv", async (DepartmentScope d, ExecutorImportService import, CancellationToken ct) =>
            Results.File(await import.TemplateAsync(d.Id, ct), "text/csv; charset=utf-8", $"sotrudniki-otdel-{d.Id}.csv"));
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
        });
        group.MapPut("/executors/{id:long}/extra", async (DepartmentScope d, long id, ExtraModeInput input,
                ConfigurationService config, CancellationToken ct) =>
            await config.SetExtraModeAsync(d.Id, id, input, ct) ? Results.NoContent() : NotFound());

        // отделы: список общий, изменения — только пустых отделов (см. DepartmentService)
        group.MapGet("/departments", (DepartmentService departments, CancellationToken ct) => departments.ListAsync(ct));
        group.MapPost("/departments", async (DepartmentInput input, DepartmentService departments, CancellationToken ct) =>
            Results.Ok(await departments.CreateAsync(input, ct)));
        group.MapPut("/departments/{id:int}",
            async (int id, DepartmentInput input, DepartmentService departments, CancellationToken ct) =>
                await departments.RenameAsync(id, input, ct) ? Results.NoContent() : NotFound());
        group.MapDelete("/departments/{id:int}", async (int id, DepartmentService departments, CancellationToken ct) =>
            await departments.DeleteAsync(id, ct) ? Results.NoContent() : NotFound());
        group.MapGet("/audit", (DepartmentScope d, long? before, int? limit, ConfigurationService config, CancellationToken ct) =>
            config.GetAuditAsync(d.Id, before, limit ?? 100, ct));
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
