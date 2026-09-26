namespace ExecutorBalancer.Application.Analytics;

/// <summary>Вес заявок, которые мог взять любой исполнитель из набора.</summary>
public sealed record FairPool(IReadOnlyList<long> Executors, decimal Weight);

/// <summary>
/// Эталон справедливого распределения — max-min fair allocation.
/// <para>
/// Дано: группы заявок с весом и набором исполнителей, которые могли их взять, и квалификации.
/// Нужно раздать вес так, чтобы нагрузка на единицу квалификации (вес / квалификация) была как можно
/// ровнее: сначала минимизируется самая большая, затем следующая и так далее. Пропорциональный делёж
/// каждой заявки для этого не годится: если один исполнитель берёт кредиты и карты, а другой только
/// кредиты, справедливо отдавать кредиты в основном второму, а пропорциональная схема сочла бы это перекосом.
/// </para>
/// <para>
/// Назначения без выбора (от родителя, вторичные) входят как неизменная «подложка» bᵢ: алгоритм выравнивает
/// полную нагрузку, и эталон тоже. Исполнитель, который упёрся в суточный лимит, больше получить не мог —
/// для него задаётся потолок: эталон не требует от него того, что запрещает лимит.
/// </para>
/// <para>
/// Такое распределение совпадает с минимумом Σ (bᵢ + xᵢ)² / qᵢ (лексикографически оптимальная база полиматроида),
/// поэтому ищется покоординатным спуском: вес каждой группы по очереди «доливается» к наименее загруженным
/// участникам, пока распределение не перестанет меняться. Задача выпуклая, спуск сходится к единственному
/// решению по нагрузкам.
/// </para>
/// </summary>
public static class FairShare
{
    private const int MaxSweeps = 500;
    private const double Tolerance = 1e-9;

    /// <param name="baseline">Вес, закреплённый за исполнителем без выбора.</param>
    /// <param name="caps">Наибольший полный вес исполнителя — для тех, кто упёрся в суточный лимит.</param>
    /// <returns>Справедливый полный вес исполнителя: подложка плюс доля из групп.</returns>
    public static Dictionary<long, decimal> Allocate(IEnumerable<FairPool> pools,
        IReadOnlyDictionary<long, decimal> qualification, IReadOnlyDictionary<long, decimal>? baseline = null,
        IReadOnlyDictionary<long, decimal>? caps = null)
    {
        var index = new Dictionary<long, int>();
        var q = new List<double>();
        var fixedLoad = new List<double>();
        var cap = new List<double>();
        var groups = new List<(int[] Members, double Weight)>();
        foreach (var pool in pools)
        {
            if (pool.Weight <= 0)
            {
                continue;
            }

            var members = pool.Executors
                .Distinct()
                .Where(id => qualification.TryGetValue(id, out var value) && value > 0)
                .Select(id =>
                {
                    if (!index.TryGetValue(id, out var i))
                    {
                        i = index[id] = q.Count;
                        q.Add((double)qualification[id]);
                        fixedLoad.Add((double)(baseline?.GetValueOrDefault(id) ?? 0));
                        cap.Add(caps is not null && caps.TryGetValue(id, out var limit) ? (double)limit : double.PositiveInfinity);
                    }

                    return i;
                })
                .ToArray();
            if (members.Length > 0)
            {
                groups.Add((members, (double)pool.Weight));
            }
        }

        var load = fixedLoad.ToArray();
        // старт — пропорциональный делёж; спуск выравнивает его дальше
        var shares = groups.Select(g =>
        {
            var total = g.Members.Sum(m => q[m]);
            var share = g.Members.Select(m => g.Weight * q[m] / total).ToArray();
            for (var k = 0; k < share.Length; k++)
            {
                load[g.Members[k]] += share[k];
            }

            return share;
        }).ToArray();

        var scale = Math.Max(1, groups.Sum(g => g.Weight));
        for (var sweep = 0; sweep < MaxSweeps; sweep++)
        {
            var change = 0d;
            for (var g = 0; g < groups.Count; g++)
            {
                change = Math.Max(change, Refill(groups[g].Members, groups[g].Weight, shares[g], load, q, cap));
            }

            if (change <= Tolerance * scale)
            {
                break;
            }
        }

        var result = index.ToDictionary(p => p.Key, p => (decimal)Math.Round(load[p.Value], 6));
        foreach (var (id, weight) in baseline ?? new Dictionary<long, decimal>())
        {
            result.TryAdd(id, weight);
        }

        return result;
    }

    /// <summary>
    /// Перераспределяет вес одной группы при неизменных остальных: уровень μ подбирается так, чтобы
    /// участники с нагрузкой ниже μ·q были долиты до него, а суммарно ушёл ровно вес группы.
    /// Возвращает наибольшее изменение доли.
    /// </summary>
    private static double Refill(int[] members, double weight, double[] share, double[] load, List<double> q,
        List<double> cap)
    {
        var n = members.Length;
        var others = new double[n];
        var room = new double[n];
        var capped = false;
        for (var k = 0; k < n; k++)
        {
            others[k] = load[members[k]] - share[k];
            room[k] = Math.Max(0, cap[members[k]] - others[k]);
            capped |= !double.IsPositiveInfinity(room[k]);
        }

        // все участники упёрлись в потолок — лишнее всё равно кому-то досталось, делим пропорционально
        if (capped && room.Sum() < weight)
        {
            capped = false;
            Array.Fill(room, double.PositiveInfinity);
        }

        var level = capped ? LevelWithCaps(members, weight, others, room, q) : Level(members, weight, others, q);
        var change = 0d;
        for (var k = 0; k < n; k++)
        {
            var next = Math.Clamp(level * q[members[k]] - others[k], 0, room[k]);
            change = Math.Max(change, Math.Abs(next - share[k]));
            load[members[k]] = others[k] + next;
            share[k] = next;
        }

        return change;
    }

    /// <summary>Уровень без потолков: точное решение по отсортированным нагрузкам.</summary>
    private static double Level(int[] members, double weight, double[] others, List<double> q)
    {
        var n = members.Length;
        var order = Enumerable.Range(0, n).OrderBy(k => others[k] / q[members[k]]).ToArray();
        double baseSum = 0, qSum = 0, level = 0;
        for (var j = 0; j < n; j++)
        {
            var k = order[j];
            baseSum += others[k];
            qSum += q[members[k]];
            level = (weight + baseSum) / qSum;
            if (j == n - 1 || level <= others[order[j + 1]] / q[members[order[j + 1]]])
            {
                break;
            }
        }

        return level;
    }

    /// <summary>Уровень с потолками: сумма долей монотонна по уровню, ищем делением пополам.</summary>
    private static double LevelWithCaps(int[] members, double weight, double[] others, double[] room, List<double> q)
    {
        double low = double.MaxValue, high = 0;
        for (var k = 0; k < members.Length; k++)
        {
            low = Math.Min(low, others[k] / q[members[k]]);
            high = Math.Max(high, (others[k] + weight) / q[members[k]]);
        }

        for (var i = 0; i < 100; i++)
        {
            var mid = (low + high) / 2;
            var total = 0d;
            for (var k = 0; k < members.Length; k++)
            {
                total += Math.Clamp(mid * q[members[k]] - others[k], 0, room[k]);
            }

            if (total < weight)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return high;
    }
}
