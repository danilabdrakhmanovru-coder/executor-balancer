using System.Globalization;
using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Api.Endpoints;

/// <summary>
/// Отдел, с которым работает запрос интерфейса: параметр <c>?department=</c>, без него — основной отдел.
/// Неверный формат — 400 при разборе параметра, несуществующий отдел — 404 в <see cref="RequireDepartment"/>.
/// </summary>
public readonly record struct DepartmentScope(int Id)
{
    public const string QueryName = "department";

    public static ValueTask<DepartmentScope?> BindAsync(HttpContext context)
    {
        var raw = context.Request.Query[QueryName];
        if (raw.Count == 0)
        {
            return ValueTask.FromResult<DepartmentScope?>(new DepartmentScope(Department.DefaultId));
        }

        return ValueTask.FromResult<DepartmentScope?>(
            raw.Count == 1 && int.TryParse(raw[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? new DepartmentScope(id)
                : null);
    }

    /// <summary>
    /// Фильтр группы: если у обработчика есть параметр отдела, отдел должен существовать и быть доступен пользователю.
    /// Чужой отдел выглядит как несуществующий (404) — список отделов не подбирается перебором.
    /// </summary>
    public static async ValueTask<object?> RequireDepartment(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
        {
            if (argument is DepartmentScope scope)
            {
                var departments = context.HttpContext.RequestServices.GetRequiredService<DepartmentService>();
                if (!context.HttpContext.User.Access().CanSee(scope.Id)
                    || !await departments.ExistsAsync(scope.Id, context.HttpContext.RequestAborted))
                {
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Отдел не найден");
                }
            }
        }

        return await next(context);
    }
}
