namespace ExecutorBalancer.Domain;

/// <summary>
/// Сводные показатели исполнителя за час. Пишутся в той же транзакции, что и решение или смена статуса,
/// поэтому отчёты не расходятся с назначениями и строятся без разбора всей истории.
/// </summary>
public class ExecutorHourStat
{
    /// <summary>Номер часа: unix-время начала часа (UTC), делённое на 3600.</summary>
    public long BucketHour { get; set; }

    public long ExecutorId { get; set; }

    /// <summary>Все назначения, включая заявки от родителя и вторичные.</summary>
    public int AssignedCount { get; set; }

    public decimal AssignedWeight { get; set; }

    /// <summary>Первичные назначения — исполнителя выбирал алгоритм.</summary>
    public int PrimaryCount { get; set; }

    /// <summary>Перераспределения — алгоритм выбирал нового исполнителя вместо прежнего.</summary>
    public int ReassignCount { get; set; }

    /// <summary>Заявки от родительской — к исполнителю родительской, без выбора.</summary>
    public int ParentCount { get; set; }

    /// <summary>Вторичные заявки — к прежнему исполнителю после доработки, без выбора.</summary>
    public int SecondaryCount { get; set; }

    /// <summary>
    /// Вес заявок свободного выбора (первичные и перераспределения). Сравнивается со справедливой долей,
    /// которую считает <c>FairShare</c> по таблице eligibility_hour_stats.
    /// </summary>
    public decimal FreeWeight { get; set; }

    /// <summary>Заявки, решённые или отклонённые исполнителем.</summary>
    public int ClosedCount { get; set; }

    /// <summary>Заявки, отправленные исполнителем на доработку.</summary>
    public int ReturnedCount { get; set; }
}
