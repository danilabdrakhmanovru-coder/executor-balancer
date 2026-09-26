using System.Globalization;
using System.Text;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Application.Analytics;

/// <summary>Приращение почасовых показателей одного исполнителя.</summary>
public sealed record StatDelta(
    long ExecutorId,
    int Assigned = 0,
    decimal AssignedWeight = 0,
    int Primary = 0,
    int Reassign = 0,
    int Parent = 0,
    int Secondary = 0,
    decimal FreeWeight = 0,
    int Closed = 0,
    int Returned = 0)
{
    public StatDelta Plus(StatDelta other) => new(
        ExecutorId,
        Assigned + other.Assigned,
        AssignedWeight + other.AssignedWeight,
        Primary + other.Primary,
        Reassign + other.Reassign,
        Parent + other.Parent,
        Secondary + other.Secondary,
        FreeWeight + other.FreeWeight,
        Closed + other.Closed,
        Returned + other.Returned);
}

/// <summary>
/// Запись в таблицу executor_hour_stats. Одна команда INSERT … ON CONFLICT DO UPDATE:
/// параллельные транзакции не теряют приращения, а строки блокируются в порядке id исполнителя —
/// без взаимных блокировок. Значения передаются только параметрами.
/// </summary>
public static class ExecutorStats
{
    private static readonly string[] Columns =
    [
        "AssignedCount", "AssignedWeight", "PrimaryCount", "ReassignCount", "ParentCount", "SecondaryCount",
        "FreeWeight", "ClosedCount", "ReturnedCount",
    ];

    private static readonly string Insert =
        "INSERT INTO executor_hour_stats (\"BucketHour\", \"ExecutorId\", " +
        string.Join(", ", Columns.Select(c => $"\"{c}\"")) + ")\nVALUES";

    private static readonly string OnConflict =
        "\nON CONFLICT (\"BucketHour\", \"ExecutorId\") DO UPDATE SET\n    " +
        string.Join(",\n    ", Columns.Select(c => $"\"{c}\" = executor_hour_stats.\"{c}\" + excluded.\"{c}\""));

    public static long HourOf(DateTimeOffset moment) =>
        (long)Math.Floor(moment.ToUnixTimeSeconds() / 3600d);

    public static DateTimeOffset HourStart(long bucket) => DateTimeOffset.FromUnixTimeSeconds(bucket * 3600);

    public static Task RecordAsync(IBalancerDbContext db, DateTimeOffset moment, IEnumerable<StatDelta> deltas,
        CancellationToken cancellationToken)
    {
        // в одной команде строка может встретиться только один раз
        var rows = deltas
            .GroupBy(d => d.ExecutorId)
            .Select(g => g.Aggregate((a, b) => a.Plus(b)))
            .OrderBy(d => d.ExecutorId)
            .ToList();
        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        var bucket = HourOf(moment);
        var sql = new StringBuilder(Insert);
        var args = new List<object>(rows.Count * (Columns.Length + 2));
        foreach (var row in rows)
        {
            sql.Append(args.Count == 0 ? "\n    (" : ",\n    (");
            for (var i = 0; i < Columns.Length + 2; i++)
            {
                sql.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? "" : ", ")}{{{args.Count + i}}}");
            }

            sql.Append(')');
            args.AddRange(
            [
                bucket, row.ExecutorId, row.Assigned, row.AssignedWeight, row.Primary, row.Reassign, row.Parent,
                row.Secondary, row.FreeWeight, row.Closed, row.Returned,
            ]);
        }

        sql.Append(OnConflict);
        return db.Database.ExecuteSqlRawAsync(sql.ToString(), args, cancellationToken);
    }

    /// <summary>
    /// Учитывает заявку свободного выбора в группе «кто мог её взять». Очень большие наборы
    /// (ключ длиннее MaxSetKeyLength) не пишутся — такие заявки просто не участвуют в эталоне.
    /// </summary>
    public static Task RecordEligibilityAsync(IBalancerDbContext db, DateTimeOffset moment,
        IEnumerable<long> eligible, decimal weight, CancellationToken cancellationToken)
    {
        var key = string.Join(',', eligible.Distinct().Order().Select(id => id.ToString(CultureInfo.InvariantCulture)));
        if (key.Length is 0 or > EligibilityHourStat.MaxSetKeyLength)
        {
            return Task.CompletedTask;
        }

        return db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO eligibility_hour_stats ("BucketHour", "SetKey", "Count", "Weight")
            VALUES ({0}, {1}, 1, {2})
            ON CONFLICT ("BucketHour", "SetKey") DO UPDATE SET
                "Count" = eligibility_hour_stats."Count" + 1,
                "Weight" = eligibility_hour_stats."Weight" + excluded."Weight"
            """,
            [HourOf(moment), key, weight],
            cancellationToken);
    }

    public static IReadOnlyList<long> ParseSetKey(string key)
    {
        var ids = new List<long>();
        foreach (var part in key.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
