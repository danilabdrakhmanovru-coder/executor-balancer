namespace ExecutorBalancer.Domain;

/// <summary>
/// История квалификации сотрудника: с какого момента действует значение. Справедливая доля за каждый час
/// считается по квалификации, действовавшей в тот час, — смена квалификации днём не искажает отчёт за утро.
/// </summary>
public class ExecutorQualification
{
    public long Id { get; set; }

    public long ExecutorId { get; set; }

    public decimal Qualification { get; set; }

    /// <summary>С какого момента действует. <see cref="DateTimeOffset.UnixEpoch"/> — «с самого начала».</summary>
    public DateTimeOffset ValidFrom { get; set; }
}
