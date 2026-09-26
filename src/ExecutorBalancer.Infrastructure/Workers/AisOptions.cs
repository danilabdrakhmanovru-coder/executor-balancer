namespace ExecutorBalancer.Infrastructure.Workers;

public sealed class AisOptions
{
    public const string Section = "Ais";
    public const string HttpClientName = "ais";

    /// <summary>Адрес АИС. Пусто — назначения в АИС не отправляются.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Ключ, с которым балансировщик вызывает АИС.</summary>
    public string ApiKey { get; set; } = "";

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan PendingRetryInterval { get; set; } = TimeSpan.FromSeconds(5);
}
