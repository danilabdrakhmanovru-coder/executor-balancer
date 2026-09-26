using System.Globalization;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Application.Balancing;

/// <summary>Качество работы сотрудника за последние дни: баллы / максимально возможные баллы.</summary>
/// <param name="Quality">От 0.1 до 1; null — закрытых заявок пока мало для оценки.</param>
public sealed record QualityInfo(int Closed, decimal ClosedWeight, decimal Points, int FastClosed, int Returned,
    decimal? Quality);

/// <summary>Сколько заявок сверх нормы сотрудник может получить сейчас и почему, если нисколько.</summary>
/// <param name="ExtraLimit">Потолок на сутки с учётом режима; null — только норма.</param>
public sealed record ExtraDecision(int? ExtraLimit, string? Note);

/// <summary>
/// Настройки мотивации отдела: рейтинг (балл = вес × коэффициент качества), режим «больше нормы»
/// и защита от работы на количество. Движок распределения их только читает.
/// </summary>
public sealed record Motivation(
    int FastCloseSeconds,
    decimal ReworkPenalty,
    decimal FastClosePenalty,
    int MaxExtraPercent,
    decimal QualityThreshold,
    decimal HeavyQualityThreshold,
    decimal HeavyWeight)
{
    /// <summary>Коэффициент качества не опускается ниже — закрытая заявка всё же сделана.</summary>
    public const decimal MinQuality = 0.1m;

    /// <summary>За сколько дней оценивается качество для режима «больше нормы».</summary>
    public const int QualityWindowDays = 7;

    /// <summary>Меньше закрытых заявок — качество ещё не оценивается.</summary>
    public const int MinClosedForQuality = 5;

    public static readonly Motivation Default = From(new Department());

    public static Motivation From(Department d) => new(d.FastCloseSeconds, d.ReworkPenalty, d.FastClosePenalty,
        d.MaxExtraPercent, d.QualityThreshold, d.HeavyQualityThreshold, d.HeavyWeight);

    /// <summary>
    /// Коэффициент качества закрытой заявки: 1 минус штраф за каждую доработку и за подозрительно
    /// быстрое закрытие, но не ниже <see cref="MinQuality"/>.
    /// </summary>
    public decimal QualityOf(int reworks, TimeSpan? handling, out bool fast)
    {
        fast = FastCloseSeconds > 0 && handling is { } time && time < TimeSpan.FromSeconds(FastCloseSeconds);
        var quality = 1m - ReworkPenalty * reworks - (fast ? FastClosePenalty : 0m);
        return Math.Max(MinQuality, quality);
    }

    public static decimal? QualityFrom(int closed, decimal closedWeight, decimal points) =>
        closed >= MinClosedForQuality && closedWeight > 0 ? Math.Round(points / closedWeight, 3) : null;

    /// <summary>
    /// Режим «больше нормы» для этой заявки: потолок не выше настройки отдела; при качестве ниже порога
    /// режим приостанавливается, сложные заявки сверх нормы — только при высоком подтверждённом качестве.
    /// </summary>
    public ExtraDecision Extra(ExecutorProfile executor, QualityInfo? quality, decimal orderWeight)
    {
        if (executor.ExtraPercent <= 0 || executor.DailyLimit is not { } limit)
        {
            return new(null, null);
        }

        var percent = Math.Min(executor.ExtraPercent, MaxExtraPercent);
        if (percent <= 0)
        {
            return new(null, "режим «больше нормы» в отделе выключен");
        }

        if (quality?.Quality is { } q && q < QualityThreshold)
        {
            return new(null, $"режим «больше нормы» приостановлен: качество {Percent(q)} ниже порога {Percent(QualityThreshold)}");
        }

        if (orderWeight >= HeavyWeight && (quality?.Quality is not { } confirmed || confirmed < HeavyQualityThreshold))
        {
            return new(null, quality?.Quality is null
                ? "сложные заявки сверх нормы — только после оценки качества"
                : $"сложные заявки сверх нормы — только при качестве от {Percent(HeavyQualityThreshold)}");
        }

        return new(limit + Math.Max(1, limit * percent / 100), null);
    }

    public static string Percent(decimal value) =>
        (value * 100m).ToString("0", CultureInfo.GetCultureInfo("ru-RU")) + "%";
}
