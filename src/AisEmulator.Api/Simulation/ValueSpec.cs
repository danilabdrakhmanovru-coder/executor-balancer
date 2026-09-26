using System.Globalization;
using System.Text.Json;

namespace AisEmulator.Api.Simulation;

/// <summary>
/// Как генерировать значение одного параметра. Эмулятор не знает сферу деятельности:
/// описание приходит вместе с командой и строится по справочнику параметров балансировщика.
/// </summary>
/// <param name="Type">String, Number, Boolean, Enum или Array.</param>
/// <param name="Weights">Вероятности значений Options (для Enum); пусто — равновероятно.</param>
/// <param name="Choices">Для Number: выбрать одно из этих чисел вместо диапазона.</param>
/// <param name="LogScale">Для Number: распределение по логарифмической шкале — много мелких, мало крупных.</param>
/// <param name="Probability">Для Boolean: вероятность true.</param>
/// <param name="Required">Для Array: значения, которые есть всегда.</param>
public sealed record ValueSpec(
    string Key,
    string Type,
    string[]? Options = null,
    double[]? Weights = null,
    decimal? Min = null,
    decimal? Max = null,
    decimal[]? Choices = null,
    bool LogScale = false,
    double? Probability = null,
    int? MinItems = null,
    int? MaxItems = null,
    string[]? Required = null)
{
    public const int MaxSpecs = 100;
    public const int MaxOptions = 100;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length > 48)
        {
            return "ключ параметра пустой или длиннее 48 символов";
        }

        if (Type is not ("String" or "Number" or "Boolean" or "Enum" or "Array"))
        {
            return $"{Key}: неизвестный тип {Type}";
        }

        if ((Options?.Length ?? 0) > MaxOptions || (Choices?.Length ?? 0) > MaxOptions || (Required?.Length ?? 0) > MaxOptions)
        {
            return $"{Key}: слишком много значений";
        }

        return Min > Max ? $"{Key}: минимум больше максимума" : null;
    }

    public JsonElement Generate(Random random) => JsonSerializer.SerializeToElement<object>(Type switch
    {
        "Number" => Number(random),
        "Boolean" => random.NextDouble() < (Probability ?? 0.5),
        "Enum" => Pick(random),
        "Array" => Subset(random),
        _ => Options is { Length: > 0 } ? Pick(random) : $"значение-{random.Next(1, 100)}",
    });

    private decimal Number(Random random)
    {
        if (Choices is { Length: > 0 })
        {
            return Choices[random.Next(Choices.Length)];
        }

        var min = (double)(Min ?? 0);
        var max = (double)(Max ?? 100);
        var value = LogScale && min > 0
            ? Math.Exp(Math.Log(min) + random.NextDouble() * (Math.Log(max) - Math.Log(min)))
            : min + random.NextDouble() * (max - min);
        // целые границы — целые значения, иначе два знака после запятой
        var integral = decimal.Truncate(Min ?? 0) == (Min ?? 0) && decimal.Truncate(Max ?? 100) == (Max ?? 100);
        return decimal.Round((decimal)value, integral ? 0 : 2);
    }

    private string Pick(Random random)
    {
        if (Options is not { Length: > 0 })
        {
            return "";
        }

        if (Weights is { Length: > 0 } && Weights.Length == Options.Length && Weights.Sum() > 0)
        {
            var roll = random.NextDouble() * Weights.Sum();
            for (var i = 0; i < Options.Length; i++)
            {
                roll -= Weights[i];
                if (roll <= 0)
                {
                    return Options[i];
                }
            }
        }

        return Options[random.Next(Options.Length)];
    }

    private string[] Subset(Random random)
    {
        var options = Options ?? [];
        var required = (Required ?? []).Where(options.Contains).ToHashSet(StringComparer.Ordinal);
        var min = Math.Clamp(MinItems ?? 1, required.Count, Math.Max(required.Count, options.Length));
        var max = Math.Clamp(MaxItems ?? options.Length, min, Math.Max(min, options.Length));
        var count = random.Next(min, max + 1);
        var rest = options.Where(o => !required.Contains(o)).OrderBy(_ => random.Next()).Take(count - required.Count);
        // порядок как в справочнике — так значения читаются в интерфейсе
        var chosen = required.Concat(rest).ToHashSet(StringComparer.Ordinal);
        return options.Where(chosen.Contains).ToArray();
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Key}:{Type}");
}
