namespace ExecutorBalancer.Application.Balancing;

/// <summary>
/// Общее для всех экземпляров сервиса хранилище нагрузки. Выбор исполнителя и увеличение
/// его счётчиков выполняются одной атомарной операцией.
/// </summary>
public interface ILoadStore
{
    Task<PickResult> PickAsync(PickRequest request, CancellationToken cancellationToken);

    /// <summary>Снимает нагрузку заявки с исполнителя. Повторный вызов ничего не делает.</summary>
    Task<bool> ReleaseAsync(long orderId, LoadRelease release, CancellationToken cancellationToken);

    Task SetActiveAsync(long executorId, bool isActive, CancellationToken cancellationToken);

    Task<long> GetConfigVersionAsync(CancellationToken cancellationToken);

    Task BumpConfigVersionAsync(CancellationToken cancellationToken);

    /// <summary>Текущая нагрузка всех исполнителей — для дашборда.</summary>
    Task<IReadOnlyDictionary<long, ExecutorLoad>> GetLoadsAsync(DateOnly day, CancellationToken cancellationToken);
}

public sealed record ExecutorLoad(long OpenWeightMilli, int OpenCount, int AssignedToday);

public enum LoadRelease
{
    /// <summary>Заявка решена или отклонена.</summary>
    Close,

    /// <summary>Заявка ушла на доработку и может вернуться.</summary>
    Await,

    /// <summary>Решение не удалось сохранить в базе — откатываем счётчики (включая суточный) и забываем заявку.</summary>
    Rollback,
}

/// <param name="DailyLimit">Норма — суточный лимит; null — без лимита или лимит не применяется.</param>
/// <param name="ExtraLimit">Потолок в режиме «больше нормы»; null — как норма. Сверх нормы исполнитель получает
/// заявку, только если ни у кого из подходящих норма ещё не набрана.</param>
public sealed record CandidateSlot(long ExecutorId, long QualificationMilli, int? DailyLimit, int? ExtraLimit = null)
{
    /// <summary>Потолок для скрипта выбора: не меньше нормы; -1 — без лимита.</summary>
    public int Cap => DailyLimit is { } limit ? Math.Max(limit, ExtraLimit ?? limit) : -1;
}

/// <param name="Reopen">Заявка возвращается в рассмотрение: прежнее назначение можно заменить.</param>
/// <param name="CountsTowardDaily">Засчитать в суточную норму. Нет — та же заявка вернулась с доработки
/// к тому же исполнителю: это не новая заявка за день.</param>
public sealed record PickRequest(long OrderId, long WeightMilli, DateOnly Day, bool Reopen,
    IReadOnlyList<CandidateSlot> Candidates, bool CountsTowardDaily = true);

public enum PickStatus
{
    Assigned,
    AlreadyAssigned,
    NoCandidate,
}

/// <param name="Verdict">eligible, over_norm (норма набрана, но режим «больше нормы» позволяет ещё),
/// inactive или daily_limit_exceeded.</param>
public sealed record SlotReport(long ExecutorId, string Verdict, long OpenWeightMilli, int AssignedToday);

/// <param name="HeldSince">С какого момента заявка закреплена за исполнителем в хранилище нагрузки.
/// Для записей, восстановленных из базы, неизвестно.</param>
public sealed record PickResult(PickStatus Status, long? ExecutorId, IReadOnlyList<SlotReport> Report,
    DateTimeOffset? HeldSince = null);

/// <summary>
/// Веса хранятся в тысячных долях целыми числами: сравнение нагрузки в Redis и в C#
/// даёт одинаковый результат без ошибок округления.
/// </summary>
public static class LoadMath
{
    public static long ToMilli(decimal value) => (long)Math.Round(value * 1000m, MidpointRounding.AwayFromZero);

    public static decimal Score(long openWeightMilli, long orderWeightMilli, long qualificationMilli) =>
        Math.Round((decimal)(openWeightMilli + orderWeightMilli) / qualificationMilli, 6);

    /// <summary>
    /// Лучше ли кандидат a, чем b. Порядок: меньший score → меньше назначений за сутки
    /// на единицу квалификации → меньший идентификатор. Сравнение без деления.
    /// </summary>
    public static bool IsBetter(long orderWeight,
        long loadA, long qualA, long dailyA, long idA,
        long loadB, long qualB, long dailyB, long idB)
    {
        var left = (loadA + orderWeight) * qualB;
        var right = (loadB + orderWeight) * qualA;
        if (left != right)
        {
            return left < right;
        }

        var dailyLeft = dailyA * qualB;
        var dailyRight = dailyB * qualA;
        return dailyLeft != dailyRight ? dailyLeft < dailyRight : idA < idB;
    }
}
