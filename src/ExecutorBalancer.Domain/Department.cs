namespace ExecutorBalancer.Domain;

/// <summary>
/// Отдел — отдельное пространство распределения: свои параметры, правила, веса, сотрудники, заявки и отчёты.
/// Номера заявок и сотрудников сквозные (их выдаёт одна АИС), каждый сотрудник числится в одном отделе.
/// </summary>
public class Department
{
    /// <summary>Отдел, в который попадают заявки и сотрудники со старых адресов интеграции без кода отдела.</summary>
    public const int DefaultId = 1;

    public int Id { get; set; }

    /// <summary>Код для адресов интеграции: /api/integration/departments/{code}/…</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Шаблон сферы, с которого отдел начинался. Параметры и правила потом можно менять свободно.</summary>
    public string? PresetId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // ---------- мотивация: рейтинг, режим «больше нормы», защита от работы на количество ----------

    /// <summary>Заявка, закрытая быстрее, считается подозрительно быстрой (0 — не проверять).</summary>
    public int FastCloseSeconds { get; set; } = 20;

    /// <summary>На сколько снижается коэффициент качества за каждую доработку заявки.</summary>
    public decimal ReworkPenalty { get; set; } = 0.25m;

    /// <summary>На сколько снижается коэффициент качества за подозрительно быстрое закрытие.</summary>
    public decimal FastClosePenalty { get; set; } = 0.5m;

    /// <summary>Потолок режима «больше нормы»: не больше стольких процентов сверх суточного лимита.</summary>
    public int MaxExtraPercent { get; set; } = 30;

    /// <summary>Качество ниже порога — режим «больше нормы» приостанавливается автоматически.</summary>
    public decimal QualityThreshold { get; set; } = 0.8m;

    /// <summary>Сложные заявки (вес не меньше <see cref="HeavyWeight"/>) сверх нормы — только при таком качестве.</summary>
    public decimal HeavyQualityThreshold { get; set; } = 0.9m;

    public decimal HeavyWeight { get; set; } = 3m;
}
