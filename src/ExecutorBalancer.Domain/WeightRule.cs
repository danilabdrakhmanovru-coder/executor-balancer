namespace ExecutorBalancer.Domain;

/// <summary>Если заявка удовлетворяет условию — её вес равен Weight. Проверяются по приоритету, срабатывает первое.</summary>
public class WeightRule
{
    public int Id { get; set; }

    public int DepartmentId { get; set; } = Department.DefaultId;
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; }
    public string OrderField { get; set; } = "";
    public RuleOperator Operator { get; set; }
    public string ValueJson { get; set; } = "null";
    public decimal Weight { get; set; }
}
