namespace ExecutorBalancer.Domain;

public class AuditEntry
{
    public long Id { get; set; }

    /// <summary>Отдел, к которому относится изменение; null — общие события (вход администратора, отделы).</summary>
    public int? DepartmentId { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string? DataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
