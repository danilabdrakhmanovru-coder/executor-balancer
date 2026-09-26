using System.Globalization;
using System.Text.Json;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Application.Rules;

/// <summary>Значение параметра, приведённое к типу из справочника полей.</summary>
public sealed class FieldValue
{
    public const int MaxTextLength = 500;
    public const int MaxItems = 100;

    private FieldValue(FieldType type, decimal number, string text, bool flag, IReadOnlyList<string> items)
    {
        Type = type;
        Number = number;
        Text = text;
        Flag = flag;
        Items = items;
    }

    public FieldType Type { get; }
    public decimal Number { get; }
    public string Text { get; }
    public bool Flag { get; }
    public IReadOnlyList<string> Items { get; }

    public bool IsArray => Type == FieldType.Array;

    /// <summary>Скалярное значение в виде строки — так оно сравнивается с элементами списка.</summary>
    public string Key => Type switch
    {
        FieldType.Number => Number.ToString(CultureInfo.InvariantCulture),
        FieldType.Boolean => Flag ? "true" : "false",
        _ => Text,
    };

    public static FieldValue FromNumber(decimal value) => new(FieldType.Number, Normalize(value), "", false, []);

    public static FieldValue FromText(string value, FieldType type = FieldType.String) => new(type, 0, value, false, []);

    public static FieldValue FromFlag(bool value) => new(FieldType.Boolean, 0, "", value, []);

    public static FieldValue FromItems(IEnumerable<string> items) =>
        new(FieldType.Array, 0, "", false, items.Distinct(StringComparer.Ordinal).ToArray());

    /// <summary>Убирает незначащие нули, чтобы 5 и 5.00 давали одинаковый ключ.</summary>
    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    public bool SameAs(FieldValue other)
    {
        if (IsArray || other.IsArray)
        {
            return IsArray && other.IsArray && Items.ToHashSet(StringComparer.Ordinal).SetEquals(other.Items);
        }

        if (Type == FieldType.Number && other.Type == FieldType.Number)
        {
            return Number == other.Number;
        }

        return string.Equals(Key, other.Key, StringComparison.Ordinal);
    }

    public override string ToString() => IsArray ? "[" + string.Join(", ", Items) + "]" : Key;

    /// <summary>null в JSON — «значение не задано»: метод вернёт true и value = null.</summary>
    public static bool TryParse(JsonElement json, FieldType type, IReadOnlyCollection<string> options,
        out FieldValue? value, out string? error)
    {
        value = null;
        error = null;
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        switch (type)
        {
            case FieldType.Number:
                if (TryNumber(json, out var number))
                {
                    value = FromNumber(number);
                    return true;
                }

                error = "ожидалось число";
                return false;

            case FieldType.Boolean:
                if (json.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    value = FromFlag(json.GetBoolean());
                    return true;
                }

                if (json.ValueKind == JsonValueKind.String && bool.TryParse(json.GetString(), out var flag))
                {
                    value = FromFlag(flag);
                    return true;
                }

                error = "ожидалось true или false";
                return false;

            case FieldType.String:
            case FieldType.Enum:
                if (!TryScalarText(json, out var text))
                {
                    error = "ожидалась строка";
                    return false;
                }

                if (text.Length > MaxTextLength)
                {
                    error = $"строка длиннее {MaxTextLength} символов";
                    return false;
                }

                if (type == FieldType.Enum && options.Count > 0 && !options.Contains(text))
                {
                    error = $"значение «{text}» отсутствует в справочнике";
                    return false;
                }

                value = FromText(text, type);
                return true;

            case FieldType.Array:
                if (json.ValueKind != JsonValueKind.Array)
                {
                    error = "ожидался массив";
                    return false;
                }

                if (json.GetArrayLength() > MaxItems)
                {
                    error = $"в массиве больше {MaxItems} элементов";
                    return false;
                }

                var items = new List<string>();
                foreach (var item in json.EnumerateArray())
                {
                    if (!TryScalarText(item, out var itemText) || itemText.Length > MaxTextLength)
                    {
                        error = "элементы массива должны быть строками или числами";
                        return false;
                    }

                    if (options.Count > 0 && !options.Contains(itemText))
                    {
                        error = $"значение «{itemText}» отсутствует в справочнике";
                        return false;
                    }

                    items.Add(itemText);
                }

                value = FromItems(items);
                return true;

            default:
                error = "неизвестный тип поля";
                return false;
        }
    }

    private static bool TryNumber(JsonElement json, out decimal number)
    {
        number = 0;
        return json.ValueKind switch
        {
            JsonValueKind.Number => json.TryGetDecimal(out number),
            JsonValueKind.String => decimal.TryParse(json.GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out number),
            _ => false,
        };
    }

    private static bool TryScalarText(JsonElement json, out string text)
    {
        if (json.ValueKind == JsonValueKind.String)
        {
            text = json.GetString()!.Trim();
            return true;
        }

        if (json.ValueKind == JsonValueKind.Number && json.TryGetDecimal(out var number))
        {
            text = Normalize(number).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        text = "";
        return false;
    }
}
