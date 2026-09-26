using System.Text.Json;
using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;

namespace ExecutorBalancer.Api.Endpoints;

public sealed record PreviewRequest(long? ParentId, Dictionary<string, JsonElement>? Attributes);

/// <summary>
/// Конструктор для администратора: параметры, правила подбора и веса, пробная проверка заявки, журнал.
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
            .AddEndpointFilter(TranslateErrors);

        group.MapGet("/config", (ConfigurationService config, CancellationToken ct) => config.GetAsync(ct));

        group.MapPost("/fields", async (FieldInput input, ConfigurationService config, CancellationToken ct) =>
            Results.Ok(await config.CreateFieldAsync(input, ct)));
        group.MapPut("/fields/{id:int}", async (int id, FieldInput input, ConfigurationService config, CancellationToken ct) =>
            await config.UpdateFieldAsync(id, input, ct) is { } field ? Results.Ok(field) : NotFound());
        group.MapDelete("/fields/{id:int}", async (int id, ConfigurationService config, CancellationToken ct) =>
            await config.DeleteFieldAsync(id, ct) ? Results.NoContent() : NotFound());

        group.MapPost("/rules", async (RuleInput input, ConfigurationService config, CancellationToken ct) =>
            Results.Ok(await config.CreateRuleAsync(input, ct)));
        group.MapPut("/rules/{id:int}", async (int id, RuleInput input, ConfigurationService config, CancellationToken ct) =>
            await config.UpdateRuleAsync(id, input, ct) is { } rule ? Results.Ok(rule) : NotFound());
        group.MapDelete("/rules/{id:int}", async (int id, ConfigurationService config, CancellationToken ct) =>
            await config.DeleteRuleAsync(id, ct) ? Results.NoContent() : NotFound());

        group.MapPost("/weight-rules", async (WeightRuleInput input, ConfigurationService config, CancellationToken ct) =>
            Results.Ok(await config.CreateWeightRuleAsync(input, ct)));
        group.MapPut("/weight-rules/{id:int}",
            async (int id, WeightRuleInput input, ConfigurationService config, CancellationToken ct) =>
                await config.UpdateWeightRuleAsync(id, input, ct) is { } rule ? Results.Ok(rule) : NotFound());
        group.MapDelete("/weight-rules/{id:int}", async (int id, ConfigurationService config, CancellationToken ct) =>
            await config.DeleteWeightRuleAsync(id, ct) ? Results.NoContent() : NotFound());

        group.MapGet("/presets", (ConfigurationService config, CancellationToken ct) => config.GetPresetsAsync(ct));
        group.MapPost("/presets/{id}/apply", async (string id, ConfigurationService config, CancellationToken ct) =>
        {
            await config.ApplyPresetAsync(id, ct);
            return Results.NoContent();
        });

        group.MapPost("/preview", Preview);
        group.MapGet("/audit", (long? before, int? limit, ConfigurationService config, CancellationToken ct) =>
            config.GetAuditAsync(before, limit ?? 100, ct));
        return app;
    }

    private static async Task<IResult> Preview(PreviewRequest request, OrderBalancer balancer, CancellationToken ct)
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

        return Results.Ok(await balancer.PreviewAsync(request.ParentId, request.Attributes ?? NoAttributes, ct));
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
