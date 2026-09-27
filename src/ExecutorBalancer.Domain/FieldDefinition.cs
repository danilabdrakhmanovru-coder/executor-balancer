namespace ExecutorBalancer.Domain;

/// <summary>Параметр заявки или исполнителя, заведённый через конструктор.</summary>
public class FieldDefinition
{
    public int Id { get; set; }

    public int DepartmentId { get; set; } = Department.DefaultId;
    public FieldOwner Owner { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; }

    /// <summary>Допустимые значения для справочников и списков. Пусто — без ограничений.</summary>
    public string[] Options { get; set; } = [];

    /// <summary>
    /// Подписи значений для экрана — по порядку <see cref="Options"/> (пусто — показывать сами значения).
    /// Коды остаются прежними: их присылает АИС и по ним работают правила; подпись — только для людей.
    /// </summary>
    public string[] OptionLabels { get; set; } = [];

    /// <summary>Как показать значение человеку: его подпись, если задана, иначе само значение.</summary>
    public string Show(string value)
    {
        var index = Array.IndexOf(Options, value);
        return index >= 0 && index < OptionLabels.Length && OptionLabels[index].Length > 0 ? OptionLabels[index] : value;
    }
}
