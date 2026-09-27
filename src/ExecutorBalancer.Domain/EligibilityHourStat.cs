namespace ExecutorBalancer.Domain;

/// <summary>
/// Поток заявок свободного выбора за пять минут, сгруппированный по набору исполнителей, которые могли их взять.
/// По этим группам строится эталон — идеально выровненное распределение того же потока
/// с теми же ограничениями, — с которым сравнивается фактическое. Пятиминутки идут по порядку: кто упёрся
/// в суточный лимит посреди часа, в следующих группах уже не участвует, и эталон не требует от выбора предвидения.
/// </summary>
public class EligibilityHourStat
{
    public const int MaxSetKeyLength = 4000;

    /// <summary>Длина отрезка внутри часа, секунд.</summary>
    public const int SlotSeconds = 300;

    /// <summary>Начало ключа строки с назначениями без выбора: «=15».</summary>
    public const string PinnedPrefix = "=";

    public int DepartmentId { get; set; } = Department.DefaultId;

    /// <summary>Номер часа: unix-время начала часа (UTC), делённое на 3600.</summary>
    public long BucketHour { get; set; }

    /// <summary>Пятиминутка внутри часа: 0–11. Записи до её появления — все в 0 (час целиком).</summary>
    public int Slot { get; set; }

    /// <summary>
    /// Идентификаторы подходивших исполнителей по возрастанию через запятую. «=id» — назначения без выбора
    /// этому исполнителю (от родителя, вторичные, сверх нормы) за эту пятиминутку.
    /// </summary>
    public string SetKey { get; set; } = "";

    public int Count { get; set; }

    public decimal Weight { get; set; }
}
