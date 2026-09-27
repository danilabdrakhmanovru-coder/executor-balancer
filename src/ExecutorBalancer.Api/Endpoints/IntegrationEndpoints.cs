using System.Text.Json;
using ExecutorBalancer.Api.Contracts;
using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
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

        // Идентификаторы заявок и исполнителей общие для всей АИС, поэтому статус и назначение
        // запрашиваются без отдела. Новые заявки, исполнители и метрики — в отдел по его коду;
        // старые адреса без отдела работают с основным отделом.
        group.MapPost("/orders", (OrderRequest request, OrderBalancer balancer, CancellationToken ct) =>
            ReceiveOrder(Department.DefaultId, request, balancer, ct));
        group.MapPost("/orders/{id:long}/status", ChangeStatus);
        group.MapGet("/orders/{id:long}/assignment", GetAssignment);
        // последний известный номер заявки: АИС, потерявшая счётчик (например, эмулятор после перезапуска),
        // продолжает нумерацию с него, а не с 1 — иначе новые заявки выглядели бы повторами старых
        group.MapGet("/orders/last-id", async (IBalancerDbContext db, CancellationToken ct) =>
            Results.Ok(new { lastId = await db.Orders.AsNoTracking().MaxAsync(o => (long?)o.Id, ct) ?? 0 }));
        group.MapPut("/executors/{id:long}",
            (long id, ExecutorRequest request, OrderBalancer balancer, CancellationToken ct) =>
                UpsertExecutor(Department.DefaultId, id, request, balancer, ct));
        group.MapGet("/metrics", (string? period, AnalyticsService analytics, CancellationToken ct) =>
            Metrics(Department.DefaultId, period, analytics, ct));

        var department = group.MapGroup("/departments/{code}");
        department.MapPost("/orders", async (string code, OrderRequest request, DepartmentService departments,
                OrderBalancer balancer, CancellationToken ct) =>
            await departments.FindByCodeAsync(code, ct) is { } id
                ? await ReceiveOrder(id, request, balancer, ct)
                : NoDepartment());
        department.MapPut("/executors/{id:long}", async (string code, long id, ExecutorRequest request,
                DepartmentService departments, OrderBalancer balancer, CancellationToken ct) =>
            await departments.FindByCodeAsync(code, ct) is { } departmentId
                ? await UpsertExecutor(departmentId, id, request, balancer, ct)
                : NoDepartment());
        // метрики дашборда для внешних систем — тот же отчёт, что в разделе «Аналитика»
        department.MapGet("/metrics", async (string code, string? period, DepartmentService departments,
                AnalyticsService analytics, CancellationToken ct) =>
            await departments.FindByCodeAsync(code, ct) is { } id
                ? await Metrics(id, period, analytics, ct)
                : NoDepartment());
        return app;
    }

    private static IResult NoDepartment() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Отдел не найден");

    private static async Task<IResult> Metrics(int departmentId, string? period, AnalyticsService analytics,
        CancellationToken ct) =>
        DashboardEndpoints.TryParsePeriod(period, out var parsed)
            ? Results.Ok(await analytics.BuildAsync(departmentId, parsed, ct))
            : DashboardEndpoints.BadPeriod();

    private static async Task<IResult> ReceiveOrder(int departmentId, OrderRequest request, OrderBalancer balancer,
        CancellationToken ct)
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
            var result = await balancer.ReceiveAsync(departmentId, incoming, ct);
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

    private static async Task<IResult> UpsertExecutor(int departmentId, long id, ExecutorRequest request, OrderBalancer balancer,
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
            await balancer.UpsertExecutorAsync(departmentId, new IncomingExecutor(id, request.FullName!.Trim(), request.IsActive,
                request.DailyLimit, request.QualificationWeight, request.Attributes ?? NoAttributes, request.ExtraPercent), ct);
            return Results.NoContent();
        }
        catch (InvalidInputException ex)
        {
            return Results.ValidationProblem(ex.Errors);
        }
    }
}
