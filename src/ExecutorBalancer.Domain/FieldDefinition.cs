namespace ExecutorBalancer.Domain;

/// <summary>Параметр заявки или исполнителя, заведённый через конструктор.</summary>
public class FieldDefinition
{
    public int Id { get; set; }
    public FieldOwner Owner { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; }

    /// <summary>Допустимые значения для справочников и списков. Пусто — без ограничений.</summary>
    public string[] Options { get; set; } = [];
}
