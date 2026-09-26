using System.Text.Json;
using System.Text.RegularExpressions;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Application.Rules;

/// <summary>Справочник параметров: какие поля есть у заявки и исполнителя и какого они типа.</summary>
public sealed partial class FieldCatalog
{
    public const int MaxAttributes = 100;

    private readonly Dictionary<(FieldOwner Owner, string Key), FieldDefinition> _fields;

    public FieldCatalog(IEnumerable<FieldDefinition> fields)
    {
        _fields = fields.ToDictionary(f => (f.Owner, f.Key));
    }

    public IReadOnlyCollection<FieldDefinition> All => _fields.Values;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,47}$")]
    private static partial Regex KeyPattern();

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public FieldDefinition? Find(FieldOwner owner, string? key) =>
        key is not null && _fields.TryGetValue((owner, key), out var field) ? field : null;

    /// <summary>
    /// Разбирает параметры по справочнику. Незнакомые ключи пропускаются: поле можно завести позже,
    /// исходный JSON сохраняется целиком. Ошибки типов добавляются в errors.
    /// </summary>
    public IReadOnlyDictionary<string, FieldValue> Parse(FieldOwner owner,
        IReadOnlyDictionary<string, JsonElement> raw, IDictionary<string, string[]>? errors)
    {
        var values = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        foreach (var (key, json) in raw)
        {
            var field = Find(owner, key);
            if (field is null)
            {
                continue;
            }

            if (FieldValue.TryParse(json, field.Type, field.Options, out var value, out var error))
            {
                if (value is not null)
                {
                    values[key] = value;
                }
            }
            else
            {
                errors?.TryAdd($"attributes.{key}", [error!]);
            }
        }

        return values;
    }

    public IReadOnlyDictionary<string, FieldValue> ParseStored(FieldOwner owner, string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new Dictionary<string, JsonElement>();
        return Parse(owner, raw, errors: null);
    }
}
