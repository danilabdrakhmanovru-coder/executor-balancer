namespace ExecutorBalancer.Application.Balancing;

public sealed class BalancerOptions
{
    public const string Section = "Balancer";

    /// <summary>Часовой пояс, по которому считаются сутки для дневного лимита.</summary>
    public string TimeZone { get; set; } = "Asia/Yekaterinburg";

    /// <summary>Вес заявки, если не сработало ни одно правило веса.</summary>
    public decimal DefaultOrderWeight { get; set; } = 1m;

    /// <summary>Как часто экземпляр сверяет версию правил и исполнителей в Redis.</summary>
    public TimeSpan ConfigCheckInterval { get; set; } = TimeSpan.FromSeconds(1);
}
