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
}
