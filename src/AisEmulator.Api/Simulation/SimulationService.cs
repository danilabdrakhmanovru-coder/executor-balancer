using System.Text.Json;

namespace AisEmulator.Api.Simulation;

/// <param name="Department">Код отдела балансировщика; null — основной отдел.</param>
/// <param name="RatePerHour">Сколько заявок в час создают «клиенты».</param>
/// <param name="HastyShare">Доля «торопливых» сотрудников: закрывают за секунды и чаще отправляют на доработку —
/// на них видно, как рейтинг и защита от работы на количество реагируют на качество.</param>
/// <param name="ParentProbability">Доля заявок, связанных с недавней заявкой (parent_id).</param>
public sealed record SimulationRequest(
    double RatePerHour,
    ValueSpec[] OrderFields,
    string? Department = null,
    double HastyShare = 0.15,
    double ParentProbability = 0.08,
    long? NextOrderId = null,
    int? StopAfterMinutes = null)
{
    public const double MaxRatePerHour = 72_000;
    public const int MaxStopAfterMinutes = 24 * 60;

    public string? Validate()
    {
        if (!AisCommands.IsValidDepartment(Department))
        {
            return "неверный код отдела";
        }

        if (RatePerHour is <= 0 or > MaxRatePerHour)
        {
            return $"скорость — от 1 до {MaxRatePerHour} заявок в час";
        }

        if (NextOrderId is < 1)
        {
            return "номер следующей заявки должен быть положительным";
        }

        if (StopAfterMinutes is < 1 or > MaxStopAfterMinutes)
        {
            return $"остановка — через 1–{MaxStopAfterMinutes} минут";
        }

        if (HastyShare is < 0 or > 1 || ParentProbability is < 0 or > 1)
        {
            return "доли — от 0 до 1";
        }

        if (OrderFields is null || OrderFields.Length > ValueSpec.MaxSpecs)
        {
            return $"параметров заявки — до {ValueSpec.MaxSpecs}";
        }

        return OrderFields.Select(f => f.Validate()).FirstOrDefault(e => e is not null);
    }
}

public sealed record SimulationStatus(
    bool Running,
    double RatePerHour,
    DateTimeOffset? StartedAt,
    long Created,
    long Accepted,
    long Rejected,
    long SentToRework,
    long Returned,
    DateTimeOffset? StopsAt = null);

/// <summary>
/// Симуляция живой АИС для демонстрации: поток заявок от клиентов и работа исполнителей
/// (решено, отклонено, на доработку и возврат с доработки). У каждого отдела свой поток —
/// их можно запускать и останавливать независимо.
/// </summary>
public sealed class SimulationService(AisStore store, AisCommands commands, ILogger<SimulationService> logger)
    : BackgroundService
{
    private const int MaxFlows = 20;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly Random _random = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Flow> _flows = new();

    /// <summary>Состояние потока одного отдела. Меняется только в шаге таймера, счётчики — атомарно.</summary>
    private sealed class Flow(string? department)
    {
        public string? Department { get; } = department;
        public SimulationRequest? Settings { get; set; }
        public DateTimeOffset? StartedAt { get; set; }

        /// <summary>Когда поток остановится сам (запуск руководителем или гостем); null — пока не остановят.</summary>
        public DateTimeOffset? StopsAt { get; set; }
        public double Due { get; set; }
        public DateTimeOffset LastWork { get; set; } = DateTimeOffset.MinValue;
        public Dictionary<long, DateTimeOffset> Rework { get; } = new();

        /// <summary>Когда сотрудник закончит заявку: за кем она и к какому моменту.</summary>
        public Dictionary<long, (long Executor, DateTimeOffset Due)> Handling { get; } = new();
        public List<long> Recent { get; } = new();
        public long Created, Accepted, Rejected, ToRework, Returned;
    }

    private static string Key(string? department) => department ?? "";

    public string? Start(SimulationRequest request)
    {
        lock (_gate)
        {
            if (!_flows.TryGetValue(Key(request.Department), out var flow))
            {
                if (_flows.Count >= MaxFlows)
                {
                    return $"одновременно — не больше {MaxFlows} потоков";
                }

                _flows[Key(request.Department)] = flow = new Flow(request.Department);
            }

            flow.Settings = request;
            flow.StartedAt ??= DateTimeOffset.UtcNow;
            flow.StopsAt = request.StopAfterMinutes is { } minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null;
        }

        logger.LogInformation("Симуляция {Department}: {Rate} заявок в час", request.Department ?? "(основной)", request.RatePerHour);
        return null;
    }

    public void Stop(string? department)
    {
        lock (_gate)
        {
            if (_flows.TryGetValue(Key(department), out var flow))
            {
                flow.Settings = null;
                flow.StartedAt = null;
                flow.StopsAt = null;
            }
        }

        logger.LogInformation("Симуляция {Department} остановлена", department ?? "(основной)");
    }

    /// <summary>Демо с чистого листа: поток отдела остановлен, счётчики и заявки в работе забыты.</summary>
    public void Reset(string? department)
    {
        lock (_gate)
        {
            _flows.Remove(Key(department));
        }

        logger.LogInformation("Симуляция {Department} сброшена", department ?? "(основной)");
    }

    public SimulationStatus Status(string? department)
    {
        lock (_gate)
        {
            if (!_flows.TryGetValue(Key(department), out var flow))
            {
                return new SimulationStatus(false, 0, null, 0, 0, 0, 0, 0);
            }

            return new SimulationStatus(flow.Settings is not null, flow.Settings?.RatePerHour ?? 0, flow.StartedAt,
                Interlocked.Read(ref flow.Created), Interlocked.Read(ref flow.Accepted), Interlocked.Read(ref flow.Rejected),
                Interlocked.Read(ref flow.ToRework), Interlocked.Read(ref flow.Returned), flow.StopsAt);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            List<(Flow Flow, SimulationRequest Settings)> running;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var flow in _flows.Values.Where(f => f.StopsAt <= now))
                {
                    logger.LogInformation("Симуляция {Department} остановлена по времени", flow.Department ?? "(основной)");
                    (flow.Settings, flow.StartedAt, flow.StopsAt) = (null, null, null);
                }

                running = _flows.Values.Where(f => f.Settings is not null).Select(f => (f, f.Settings!)).ToList();
            }

            foreach (var (flow, settings) in running)
            {
                try
                {
                    Step(flow, settings);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Ошибка шага симуляции");
                }
            }
        }
    }

    private void Step(Flow flow, SimulationRequest settings)
    {
        // поток заявок: копим «долю заявки» за каждый тик, создаём целые
        flow.Due += settings.RatePerHour / 3600 * Tick.TotalSeconds;
        while (flow.Due >= 1)
        {
            flow.Due -= 1;
            CreateOrder(flow, settings);
        }

        var now = DateTimeOffset.UtcNow;
        if (now - flow.LastWork >= TimeSpan.FromSeconds(1))
        {
            flow.LastWork = now;
            Work(flow, settings, now);
        }
    }

    private void CreateOrder(Flow flow, SimulationRequest settings)
    {
        var attributes = settings.OrderFields.ToDictionary(f => f.Key, f => f.Generate(_random));
        long? parentId = flow.Recent.Count > 0 && _random.NextDouble() < settings.ParentProbability
            ? flow.Recent[_random.Next(flow.Recent.Count)]
            : null;
        var order = commands.CreateOrder(flow.Department, parentId, attributes);
        flow.Recent.Add(order.Id);
        if (flow.Recent.Count > 200)
        {
            flow.Recent.RemoveAt(0);
        }

        Interlocked.Increment(ref flow.Created);
    }

    /// <summary>
    /// «Торопливый» сотрудник — детерминированно по номеру, чтобы на демонстрации это были одни и те же люди.
    /// Поведение сотрудников — только имитация; балансировщик о нём не знает и видит лишь сроки и статусы.
    /// </summary>
    public static bool IsHasty(long executorId, double share) => (ulong)executorId * 2654435761UL % 100 < share * 100;

    /// <summary>
    /// Исполнители работают: заявка, назначение которой уже записано в АИС, решается через время обработки.
    /// Обычный сотрудник тратит 25–90 с и редко отправляет на доработку; торопливый — 2–10 с и чаще.
    /// </summary>
    private void Work(Flow flow, SimulationRequest settings, DateTimeOffset now)
    {
        var inWork = new HashSet<long>();
        foreach (var order in store.Orders("processed", assigned: true, limit: 2000, department: flow.Department ?? AisStore.NoDepartment))
        {
            var executor = order.ExecutorId!.Value;
            if (flow.Rework.ContainsKey(order.Id))
            {
                continue;
            }

            inWork.Add(order.Id);
            var hasty = IsHasty(executor, settings.HastyShare);
            if (!flow.Handling.TryGetValue(order.Id, out var plan) || plan.Executor != executor)
            {
                var seconds = hasty ? 2 + _random.NextDouble() * 8 : 25 + _random.NextDouble() * 65;
                flow.Handling[order.Id] = (executor, now + TimeSpan.FromSeconds(seconds));
                continue;
            }

            if (plan.Due > now)
            {
                continue;
            }

            flow.Handling.Remove(order.Id);
            inWork.Remove(order.Id);
            var roll = _random.NextDouble();
            var rework = hasty ? 0.4 : 0.12;
            var status = roll < rework ? "await" : roll < rework + 0.12 ? "reject" : "accept";
            commands.SetStatus(order.Id, status);
            switch (status)
            {
                case "accept": Interlocked.Increment(ref flow.Accepted); break;
                case "reject": Interlocked.Increment(ref flow.Rejected); break;
                default:
                    Interlocked.Increment(ref flow.ToRework);
                    flow.Rework[order.Id] = now + TimeSpan.FromSeconds(5 + _random.NextDouble() * 15);
                    break;
            }
        }

        // заявки, которые ушли из работы помимо симуляции (перераспределены, закрыты вручную), больше не ждём
        foreach (var id in flow.Handling.Keys.Where(id => !inWork.Contains(id)).ToList())
        {
            flow.Handling.Remove(id);
        }

        // клиент дописал заявку — она возвращается в рассмотрение
        foreach (var (id, _) in flow.Rework.Where(p => p.Value <= now).ToList())
        {
            flow.Rework.Remove(id);
            commands.SetStatus(id, "processed");
            Interlocked.Increment(ref flow.Returned);
        }
    }
}

/// <summary>
/// Завести исполнителей отдела для демонстрации: ID FirstId..FirstId+Count-1 (у каждого отдела свой диапазон —
/// идентификаторы в АИС общие). Без <see cref="KeepOthers"/> прежние исполнители отдела вне диапазона деактивируются;
/// с ним — добавляются к имеющимся, а имена продолжаются с <see cref="NameOffset"/>.
/// </summary>
public sealed record SeedExecutorsRequest(
    int Count,
    ValueSpec[] Fields,
    string? Department = null,
    long FirstId = 1,
    string[]? Names = null,
    int?[]? DailyLimits = null,
    decimal[]? Qualifications = null,
    bool KeepOthers = false,
    int NameOffset = 0)
{
    public const int MaxCount = 100;

    public string? Validate()
    {
        if (!AisCommands.IsValidDepartment(Department) || FirstId is < 1 or > 1_000_000_000 || NameOffset is < 0 or > 1_000_000)
        {
            return "неверный отдел или первый ID";
        }

        if (Count is < 1 or > MaxCount)
        {
            return $"исполнителей — от 1 до {MaxCount}";
        }

        if (Fields is null || Fields.Length > ValueSpec.MaxSpecs || (Names?.Length ?? 0) > 1000)
        {
            return "слишком много параметров или имён";
        }

        if (Qualifications?.Any(q => q is < 0.1m or > 100m) == true || DailyLimits?.Any(l => l is < 0 or > 100_000) == true)
        {
            return "квалификация — от 0.1 до 100, лимит — от 0 до 100000";
        }

        return Fields.Select(f => f.Validate()).FirstOrDefault(e => e is not null);
    }

    public IReadOnlyList<AisExecutor> Build(Random random)
    {
        var names = Names is { Length: > 0 } ? Names : ["Исполнитель"];
        return Enumerable.Range(1, Count).Select(i => (Index: i, Name: i + NameOffset)).Select(x => new AisExecutor
        {
            Id = FirstId + x.Index - 1,
            Department = Department,
            // имён меньше, чем сотрудников: первый круг — как есть, дальше с номером круга («Иванов И. 2»)
            FullName = x.Name <= names.Length ? names[x.Name - 1] : $"{names[(x.Name - 1) % names.Length]} {(x.Name - 1) / names.Length + 1}",
            IsActive = true,
            DailyLimit = DailyLimits is { Length: > 0 } ? DailyLimits[random.Next(DailyLimits.Length)] : null,
            QualificationWeight = Qualifications is { Length: > 0 } ? Qualifications[random.Next(Qualifications.Length)] : 1m,
            Attributes = Fields.ToDictionary(f => f.Key, f => f.Generate(random)),
        }).ToList();
    }
}
