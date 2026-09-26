using System.Text.Json;
using ExecutorBalancer.Api.Contracts;
using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Api.Endpoints;

/// <summary>Вызовы из внешней АИС: новые заявки, смена статусов, изменения исполнителей.</summary>
public static class IntegrationEndpoints
{
    private static readonly Dictionary<string, JsonElement> NoAttributes = new();

    public static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integration")
            .WithTags("Интеграция с АИС")
            .AddEndpointFilter<ApiKeyFilter>()
            .RequireRateLimiting(RateLimits.Integration);

        group.MapPost("/orders", ReceiveOrder);
        group.MapPost("/orders/{id:long}/status", ChangeStatus);
        group.MapGet("/orders/{id:long}/assignment", GetAssignment);
        group.MapPut("/executors/{id:long}", UpsertExecutor);
        return app;
    }

    private static async Task<IResult> ReceiveOrder(OrderRequest request, OrderBalancer balancer, CancellationToken ct)
    {
        var errors = RequestValidation.Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var status = RequestValidation.TryParseStatus(request.Status, out var parsed) ? parsed : OrderStatus.Processed;
        var incoming = new IncomingOrder(request.Id, request.ParentId, status, request.Attributes ?? NoAttributes);
        try
        {
            var result = await balancer.ReceiveAsync(incoming, ct);
            return Results.Ok(result);
        }
        catch (InvalidInputException ex)
        {
            return Results.ValidationProblem(ex.Errors);
        }
    }

    private static async Task<IResult> ChangeStatus(long id, StatusRequest request, OrderBalancer balancer,
        CancellationToken ct)
    {
        if (!RequestValidation.TryParseStatus(request.Status, out var status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = ["допустимо: processed, await, accept, reject"],
            });
        }

        var result = await balancer.ChangeStatusAsync(id, status, ct);
        return result is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Заявка не найдена")
            : Results.Ok(result);
    }

    private static async Task<IResult> GetAssignment(long id, IBalancerDbContext db, CancellationToken ct)
    {
        var assignment = await db.Assignments.AsNoTracking()
            .Where(a => a.OrderId == id && a.IsCurrent)
            .FirstOrDefaultAsync(ct);
        if (assignment is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Назначения нет");
        }

        return Results.Ok(new
        {
            assignment.OrderId,
            assignment.ExecutorId,
            Kind = assignment.Kind.ToString(),
            assignment.Score,
            assignment.CreatedAt,
            Explanation = AssignmentExplanation.FromJson(assignment.ExplanationJson),
        });
    }

    private static async Task<IResult> UpsertExecutor(long id, ExecutorRequest request, OrderBalancer balancer,
        CancellationToken ct)
    {
        var errors = RequestValidation.Validate(request);
        if (id <= 0)
        {
            errors["id"] = ["должен быть положительным"];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        try
        {
            await balancer.UpsertExecutorAsync(new IncomingExecutor(id, request.FullName!.Trim(), request.IsActive,
                request.DailyLimit, request.QualificationWeight, request.Attributes ?? NoAttributes), ct);
            return Results.NoContent();
        }
        catch (InvalidInputException ex)
        {
            return Results.ValidationProblem(ex.Errors);
        }
    }
}
