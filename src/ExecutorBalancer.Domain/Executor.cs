namespace ExecutorBalancer.Domain;

/// <summary>Копия исполнителя из АИС плюс настройки, которые задаются в балансировщике.</summary>
public class Executor
{
    public long Id { get; set; }
    public string FullName { get; set; } = "";
    public bool IsActive { get; set; }

    /// <summary>null — лимита нет.</summary>
    public int? DailyLimit { get; set; }

    public decimal QualificationWeight { get; set; } = 1m;
    public string AttributesJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}
