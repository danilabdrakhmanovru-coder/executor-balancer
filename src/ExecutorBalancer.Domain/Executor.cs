namespace ExecutorBalancer.Domain;

/// <summary>Копия исполнителя из АИС плюс настройки, которые задаются в балансировщике.</summary>
public class Executor
{
    public long Id { get; set; }

    public int DepartmentId { get; set; } = Department.DefaultId;
    public string FullName { get; set; } = "";
    public bool IsActive { get; set; }

    /// <summary>null — лимита нет.</summary>
    public int? DailyLimit { get; set; }

    public decimal QualificationWeight { get; set; } = 1m;

    /// <summary>
    /// Режим «готов взять больше нормы»: на сколько процентов сверх суточного лимита сотрудник готов работать.
    /// 0 — режим выключен. Сверх нормы достаются только излишки — заявки, которые иначе ждали бы.
    /// </summary>
    public int ExtraPercent { get; set; }
    public string AttributesJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}
