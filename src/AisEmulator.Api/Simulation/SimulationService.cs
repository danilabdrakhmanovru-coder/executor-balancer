using System.Text.Json;

namespace AisEmulator.Api.Simulation;

/// <param name="RatePerHour">Сколько заявок в час создают «клиенты».</param>
/// <param name="WorkPerSecond">Доля назначенных заявок, которые исполнители обрабатывают за секунду.</param>
/// <param name="ParentProbability">Доля заявок, связанных с недавней заявкой (parent_id).</param>
public sealed record SimulationRequest(
    double RatePerHour,
    ValueSpec[] OrderFields,
    double WorkPerSecond = 0.15,
    double ParentProbability = 0.08)
{
    public const double MaxRatePerHour = 72_000;

    public string? Validate()
    {
        if (RatePerHour is <= 0 or > MaxRatePerHour)
        {
            return $"скорость — от 1 до {MaxRatePerHour} заявок в час";
        }

        if (WorkPerSecond is < 0 or > 1 || ParentProbability is < 0 or > 1)
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
    long Returned);

/// <summary>
/// Симуляция живой АИС для демонстрации: поток заявок от клиентов и работа исполнителей
/// (решено, отклонено, на доработку и возврат с доработки). Включается и выключается через API.
/// </summary>
public sealed class SimulationService(AisStore store, AisCommands commands, ILogger<SimulationService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly Random _random = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, DateTimeOffset> _rework = new();
    private readonly List<long> _recent = new();
    private SimulationRequest? _settings;
    private DateTimeOffset? _startedAt;
    private double _due;
    private DateTimeOffset _lastWork = DateTimeOffset.MinValue;
    private long _created, _accepted, _rejected, _toRework, _returned;

    public void Start(SimulationRequest request)
    {
        lock (_gate)
        {
            _settings = request;
            _startedAt ??= DateTimeOffset.UtcNow;
        }

        logger.LogInformation("Симуляция: {Rate} заявок в час", request.RatePerHour);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _settings = null;
            _startedAt = null;
        }

        logger.LogInformation("Симуляция остановлена");
    }

    public SimulationStatus Status()
    {
        lock (_gate)
        {
            return new SimulationStatus(_settings is not null, _settings?.RatePerHour ?? 0, _startedAt,
                Interlocked.Read(ref _created), Interlocked.Read(ref _accepted), Interlocked.Read(ref _rejected),
                Interlocked.Read(ref _toRework), Interlocked.Read(ref _returned));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                Step();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ошибка шага симуляции");
            }
        }
    }

    private void Step()
    {
        SimulationRequest? settings;
        lock (_gate)
        {
            settings = _settings;
        }

        if (settings is null)
        {
            return;
        }

        // поток заявок: копим «долю заявки» за каждый тик, создаём целые
        _due += settings.RatePerHour / 3600 * Tick.TotalSeconds;
        while (_due >= 1)
        {
            _due -= 1;
            CreateOrder(settings);
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastWork >= TimeSpan.FromSeconds(1))
        {
            _lastWork = now;
            Work(settings, now);
        }
    }

    private void CreateOrder(SimulationRequest settings)
    {
        var attributes = settings.OrderFields.ToDictionary(f => f.Key, f => f.Generate(_random));
        long? parentId = _recent.Count > 0 && _random.NextDouble() < settings.ParentProbability
            ? _recent[_random.Next(_recent.Count)]
            : null;
        var order = commands.CreateOrder(parentId, attributes);
        _recent.Add(order.Id);
        if (_recent.Count > 200)
        {
            _recent.RemoveAt(0);
        }

        Interlocked.Increment(ref _created);
    }

    /// <summary>Исполнители работают: назначение уже записано в АИС — заявку можно решить.</summary>
    private void Work(SimulationRequest settings, DateTimeOffset now)
    {
        foreach (var order in store.Orders("processed", assigned: true, limit: 1000))
        {
            if (_rework.ContainsKey(order.Id) || _random.NextDouble() >= settings.WorkPerSecond)
            {
                continue;
            }

            var roll = _random.NextDouble();
            var status = roll < 0.65 ? "accept" : roll < 0.75 ? "reject" : "await";
            commands.SetStatus(order.Id, status);
            switch (status)
            {
                case "accept": Interlocked.Increment(ref _accepted); break;
                case "reject": Interlocked.Increment(ref _rejected); break;
                default:
                    Interlocked.Increment(ref _toRework);
                    _rework[order.Id] = now + TimeSpan.FromSeconds(5 + _random.NextDouble() * 15);
                    break;
            }
        }

        // клиент дописал заявку — она возвращается в рассмотрение
        foreach (var (id, _) in _rework.Where(p => p.Value <= now).ToList())
        {
            _rework.Remove(id);
            commands.SetStatus(id, "processed");
            Interlocked.Increment(ref _returned);
        }
    }
}

/// <summary>Завести исполнителей для демонстрации: ID 1..Count, остальные деактивируются.</summary>
public sealed record SeedExecutorsRequest(
    int Count,
    ValueSpec[] Fields,
    string[]? Names = null,
    int?[]? DailyLimits = null,
    decimal[]? Qualifications = null)
{
    public const int MaxCount = 100;

    public string? Validate()
    {
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
        return Enumerable.Range(1, Count).Select(id => new AisExecutor
        {
            Id = id,
            FullName = names.Length >= Count ? names[id - 1] : $"{names[(id - 1) % names.Length]} {id}",
            IsActive = true,
            DailyLimit = DailyLimits is { Length: > 0 } ? DailyLimits[random.Next(DailyLimits.Length)] : null,
            QualificationWeight = Qualifications is { Length: > 0 } ? Qualifications[random.Next(Qualifications.Length)] : 1m,
            Attributes = Fields.ToDictionary(f => f.Key, f => f.Generate(random)),
        }).ToList();
    }
}
