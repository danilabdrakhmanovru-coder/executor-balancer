using System.Text.Json;
using System.Text.RegularExpressions;

namespace AisEmulator.Api;

/// <summary>
/// Действия АИС, о которых нужно сообщить балансировщику: запись в хранилище и постановка события в очередь.
/// Одни и те же для HTTP API эмулятора и для симуляции потока.
/// </summary>
public sealed partial class AisCommands(AisStore store, BalancerForwarder forwarder)
{
    /// <summary>Код отдела балансировщика — тот же формат, что проверяет балансировщик; попадает в адрес запроса.</summary>
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")]
    private static partial Regex DepartmentPattern();

    public static bool IsValidDepartment(string? code) => code is null || DepartmentPattern().IsMatch(code);

    public AisOrder CreateOrder(string? department, long? parentId, Dictionary<string, JsonElement> attributes)
    {
        var order = store.CreateOrder(department, parentId, attributes);
        forwarder.Enqueue(new ForwardCommand(HttpMethod.Post, Route(department, "orders"),
            new { id = order.Id, parentId = order.ParentId, attributes = order.Attributes }));
        return order;
    }

    public AisOrder? SetStatus(long id, string status)
    {
        var order = store.SetStatus(id, status);
        if (order is not null)
        {
            forwarder.Enqueue(new ForwardCommand(HttpMethod.Post, $"api/integration/orders/{id}/status", new { status }));
        }

        return order;
    }

    public AisExecutor UpsertExecutor(AisExecutor executor)
    {
        var saved = store.UpsertExecutor(executor);
        Forward(saved);
        return saved;
    }

    public AisExecutor? SetExecutorActive(long id, bool isActive)
    {
        var executor = store.SetExecutorActive(id, isActive);
        if (executor is not null)
        {
            Forward(executor);
        }

        return executor;
    }

    private void Forward(AisExecutor e) =>
        forwarder.Enqueue(new ForwardCommand(HttpMethod.Put, Route(e.Department, $"executors/{e.Id}"), new
        {
            fullName = e.FullName,
            isActive = e.IsActive,
            dailyLimit = e.DailyLimit,
            qualificationWeight = e.QualificationWeight,
            attributes = e.Attributes,
        }));

    /// <summary>Заявки и исполнители отдела — в его адрес; без отдела — старый адрес основного отдела.</summary>
    private static string Route(string? department, string path) =>
        department is null ? $"api/integration/{path}" : $"api/integration/departments/{department}/{path}";
}
