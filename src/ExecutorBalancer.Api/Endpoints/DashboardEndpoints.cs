using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Api.Endpoints;

/// <summary>Данные для дашборда. Только чтение, только для вошедшего администратора.</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Дашборд").RequireAuthorization();
        group.MapGet("/summary", Summary);
        group.MapGet("/feed", Feed);
        group.MapGet("/orders/{id:long}", OrderDetails);
        return app;
    }

    private static async Task<IResult> Summary(IBalancerDbContext db, ExecutorDirectory directory, ILoadStore loads,
        OrderBalancer balancer, CancellationToken ct)
    {
        var snapshot = await directory.GetAsync(ct);
        var today = balancer.Today();
        var current = await loads.GetLoadsAsync(today, ct);
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);

        var pending = await db.Orders.CountAsync(o => o.Status == OrderStatus.Processed && o.ExecutorId == null, ct);
        var open = await db.Orders.CountAsync(o => o.Status == OrderStatus.Processed && o.ExecutorId != null, ct);
        var lastHour = await db.Assignments.CountAsync(a => a.CreatedAt >= hourAgo, ct);
        var total = await db.Orders.CountAsync(ct);
        var undelivered = await db.OutboxMessages.CountAsync(m => m.SentAt == null, ct);

        var executors = snapshot.Executors.Values
            .OrderBy(e => e.Id)
            .Select(e =>
            {
                var load = current.GetValueOrDefault(e.Id) ?? new ExecutorLoad(0, 0, 0);
                var openWeight = load.OpenWeightMilli / 1000m;
                return new
                {
                    e.Id,
                    e.FullName,
                    e.IsActive,
                    e.DailyLimit,
                    e.QualificationWeight,
                    OpenCount = load.OpenCount,
                    OpenWeight = openWeight,
                    load.AssignedToday,
                    // нагрузка на единицу квалификации — именно её выравнивает алгоритм
                    RelativeLoad = Math.Round(openWeight / e.QualificationWeight, 3),
                };
            })
            .ToList();

        var active = executors.Where(e => e.IsActive).Select(e => e.RelativeLoad).ToList();
        return Results.Ok(new
        {
            Totals = new
            {
                Orders = total,
                Open = open,
                Pending = pending,
                AssignedLastHour = lastHour,
                Undelivered = undelivered,
                ActiveExecutors = active.Count,
                Spread = active.Count > 1 ? active.Max() - active.Min() : 0m,
            },
            RuleErrors = snapshot.RuleErrors,
            Executors = executors,
        });
    }

    private static async Task<IResult> Feed(long? after, IBalancerDbContext db, ExecutorDirectory directory,
        CancellationToken ct)
    {
        var snapshot = await directory.GetAsync(ct);
        var query = db.Assignments.AsNoTracking();
        if (after is { } afterId)
        {
            query = query.Where(a => a.Id > afterId);
        }

        var rows = await query.OrderByDescending(a => a.Id).Take(50)
            .Select(a => new { a.Id, a.OrderId, a.ExecutorId, a.Kind, a.Score, a.OrderWeight, a.CreatedAt })
            .ToListAsync(ct);

        return Results.Ok(rows.Select(a => new
        {
            a.Id,
            a.OrderId,
            a.ExecutorId,
            ExecutorName = snapshot.Executors.TryGetValue(a.ExecutorId, out var e) ? e.FullName : $"#{a.ExecutorId}",
            Kind = a.Kind.ToString(),
            a.Score,
            a.OrderWeight,
            a.CreatedAt,
        }));
    }

    private static async Task<IResult> OrderDetails(long id, IBalancerDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Заявка не найдена");
        }

        var history = await db.Assignments.AsNoTracking()
            .Where(a => a.OrderId == id)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            order.Id,
            order.ParentId,
            Status = order.Status.ToString(),
            order.Weight,
            order.ExecutorId,
            order.PendingReason,
            order.ReceivedAt,
            Attributes = System.Text.Json.JsonDocument.Parse(order.AttributesJson).RootElement,
            History = history.Select(a => new
            {
                a.Id,
                a.ExecutorId,
                Kind = a.Kind.ToString(),
                a.Score,
                a.IsCurrent,
                a.CreatedAt,
                Explanation = AssignmentExplanation.FromJson(a.ExplanationJson),
            }),
        });
    }
}
