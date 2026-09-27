using System.Globalization;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Application.Rules;

/// <summary>
/// Значение параметра для человека: числа — с разрядами, «да/нет», а коды справочника — подписями
/// (<see cref="FieldDefinition.OptionLabels"/>): ORDER_3 → «Претензия». Правила и АИС работают с кодами.
/// </summary>
public static class FieldText
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string Show(FieldDefinition? field, FieldValue value) => value.IsArray
        ? string.Join(", ", Items(field, value))
        : value.Type switch
        {
            FieldType.Number => value.Number.ToString("#,0.##", Russian),
            FieldType.Boolean => value.Flag ? "да" : "нет",
            _ => field?.Show(value.Text) ?? value.Text,
        };

    public static IEnumerable<string> Items(FieldDefinition? field, FieldValue value) =>
        value.Items.Select(item => field?.Show(item) ?? item);
}
