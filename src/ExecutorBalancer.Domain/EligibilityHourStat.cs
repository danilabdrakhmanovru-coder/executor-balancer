namespace ExecutorBalancer.Domain;

/// <summary>
/// Поток заявок свободного выбора за час, сгруппированный по набору исполнителей, которые могли их взять.
/// По этим группам строится эталон — идеально выровненное распределение того же потока
/// с теми же ограничениями, — с которым сравнивается фактическое.
/// </summary>
public class EligibilityHourStat
{
    public const int MaxSetKeyLength = 4000;

    /// <summary>Номер часа: unix-время начала часа (UTC), делённое на 3600.</summary>
    public long BucketHour { get; set; }

    /// <summary>Идентификаторы подходивших исполнителей по возрастанию через запятую.</summary>
    public string SetKey { get; set; } = "";

    public int Count { get; set; }

    public decimal Weight { get; set; }
}
