using System.Text.Json;

namespace AisEmulator.Api;

/// <summary>
/// Действия АИС, о которых нужно сообщить балансировщику: запись в хранилище и постановка события в очередь.
/// Одни и те же для HTTP API эмулятора и для симуляции потока.
/// </summary>
public sealed class AisCommands(AisStore store, BalancerForwarder forwarder)
{
    public AisOrder CreateOrder(long? parentId, Dictionary<string, JsonElement> attributes)
    {
        var order = store.CreateOrder(parentId, attributes);
        forwarder.Enqueue(new ForwardCommand(HttpMethod.Post, "api/integration/orders",
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
        forwarder.Enqueue(new ForwardCommand(HttpMethod.Put, $"api/integration/executors/{e.Id}", new
        {
            fullName = e.FullName,
            isActive = e.IsActive,
            dailyLimit = e.DailyLimit,
            qualificationWeight = e.QualificationWeight,
            attributes = e.Attributes,
        }));
}
