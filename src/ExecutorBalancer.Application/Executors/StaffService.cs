using System.Globalization;
using System.Text.Json;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Application.Users;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Application.Executors;

/// <param name="Back">Кто вернулся на работу.</param>
/// <param name="ExtraCleared">У скольких выключен режим «больше нормы».</param>
/// <param name="Missing">Скольких не хватает до численности, заведённой администратором.</param>
/// <param name="Orders">Сколько заявок отдела удалено вместе с назначениями и статистикой.</param>
public sealed record DemoResetResult(IReadOnlyList<long> Back, int ExtraCleared, int Missing, int Orders = 0);

/// <summary>
/// Состав отдела из интерфейса: перерыв, возвращение на работу, увольнение. Уйти может не всякий: на работе должна
/// остаться доля отдела (<see cref="Department.MinOnDutyPercent"/>) и хотя бы один сотрудник, способный взять каждый
/// вид заявок, который брать сейчас есть кому. Открытые заявки ушедшего перераспределяются, всё пишется в журнал.
/// </summary>
public sealed class StaffService(
    IBalancerDbContext db,
    ExecutorDirectory directory,
    OrderBalancer balancer,
    ILoadStore loadStore,
    ICurrentActor actor,
    TimeProvider clock)
{
    private const int MaxGapsShown = 5;

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    /// <summary>Перерыв (false) или возвращение на работу (true). null — сотрудника в отделе нет.</summary>
    public async Task<bool?> SetActiveAsync(int departmentId, long executorId, bool isActive, CancellationToken cancellationToken)
    {
        var executor = await db.Executors.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executorId && e.DepartmentId == departmentId, cancellationToken);
        if (executor is null)
        {
            return null;
        }

        if (executor.IsActive == isActive)
        {
            return isActive;
        }

        if (!isActive)
        {
            await EnsureCanLeaveAsync(departmentId, executor, dismissal: false, cancellationToken);
        }

        await balancer.UpsertExecutorAsync(departmentId, Incoming(executor, isActive), cancellationToken);
        await AuditAsync(departmentId, isActive ? "executor_back" : "executor_break", executor, cancellationToken);
        return isActive;
    }

    /// <summary>Увольнение: открытые заявки — коллегам, сотрудник удаляется. История назначений остаётся.</summary>
    /// <param name="force">Без проверки состава смены — когда администратор тестового стенда сам задаёт численность отдела.</param>
    public async Task<bool> DismissAsync(int departmentId, long executorId, CancellationToken cancellationToken, bool force = false)
    {
        var executor = await db.Executors.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executorId && e.DepartmentId == departmentId, cancellationToken);
        if (executor is null)
        {
            return false;
        }

        if (executor.IsActive)
        {
            if (!force)
            {
                await EnsureCanLeaveAsync(departmentId, executor, dismissal: true, cancellationToken);
            }

            // как уход с работы: открытые заявки уходят коллегам
            await balancer.UpsertExecutorAsync(departmentId, Incoming(executor, false), cancellationToken);
        }

        await db.Executors.Where(e => e.Id == executorId).ExecuteDeleteAsync(cancellationToken);
        await loadStore.SetActiveAsync(executorId, false, cancellationToken);
        await loadStore.BumpConfigVersionAsync(cancellationToken);
        directory.Invalidate();
        await AuditAsync(departmentId, "executor_dismissed", executor, cancellationToken);
        return true;
    }

    /// <summary>
    /// Демо после гостей — как с чистого листа: заявки отдела, назначения, история статусов и статистика удаляются,
    /// нагрузка обнуляется, все сотрудники — на работу, режим «больше нормы» выключен. Возвращает, кто вернулся
    /// на работу и скольких не хватает до численности, заведённой администратором (их заводит пульт через АИС).
    /// Настройки, правила и журнал остаются. null — отдела нет. Поток в АИС пульт останавливает до вызова.
    /// </summary>
    public async Task<DemoResetResult?> ResetDemoAsync(int departmentId, bool automatic, CancellationToken cancellationToken)
    {
        var department = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, cancellationToken);
        if (department is null)
        {
            return null;
        }

        var staff = await db.Executors.AsNoTracking().Where(e => e.DepartmentId == departmentId).ToListAsync(cancellationToken);
        var orderIds = await DepartmentData.WipeOrdersAsync(db, departmentId, cancellationToken);
        await loadStore.ForgetAsync(staff.Select(e => e.Id).ToList(), orderIds, balancer.Today(), balancer.CurrentHour(),
            cancellationToken);

        var back = staff.Where(e => !e.IsActive).ToList();
        foreach (var executor in back)
        {
            await balancer.UpsertExecutorAsync(departmentId, Incoming(executor, true), cancellationToken);
        }

        var extra = await db.Executors.Where(e => e.DepartmentId == departmentId && e.ExtraPercent > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ExtraPercent, 0), cancellationToken);
        await loadStore.BumpConfigVersionAsync(cancellationToken);
        directory.Invalidate();

        var missing = Math.Max(0, (department.DemoStaffCount ?? staff.Count) - staff.Count);
        if (back.Count > 0 || extra > 0 || missing > 0 || orderIds.Count > 0)
        {
            db.AuditEntries.Add(new AuditEntry
            {
                DepartmentId = departmentId,
                Actor = actor.Name,
                Action = "demo_reset",
                Entity = "department",
                EntityId = departmentId.ToString(CultureInfo.InvariantCulture),
                DataJson = JsonSerializer.Serialize(new { after = new { back = back.Count, extra, missing, orders = orderIds.Count, automatic } }, AuditJson),
                CreatedAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        return new DemoResetResult(back.Select(e => e.Id).ToList(), extra, missing, orderIds.Count);
    }

    /// <summary>
    /// Можно ли снять сотрудника с работы: останется ли на работе нужная доля отдела и не останется ли вид заявок,
    /// который сейчас есть кому брать, без единого исполнителя. Нельзя — <see cref="ConfigurationConflictException"/>.
    /// </summary>
    private async Task EnsureCanLeaveAsync(int departmentId, Executor executor, bool dismissal, CancellationToken cancellationToken)
    {
        var department = await db.Departments.AsNoTracking().FirstAsync(d => d.Id == departmentId, cancellationToken);
        var snapshot = await directory.GetAsync(departmentId, cancellationToken);
        var active = snapshot.Executors.Values.Where(e => e.IsActive && e.Id != executor.Id).ToList();
        var total = snapshot.Executors.Count - (dismissal ? 1 : 0);
        var required = Math.Max(1, (int)Math.Ceiling(total * department.MinOnDutyPercent / 100m));
        var what = dismissal ? "уволить" : "отправить на перерыв";
        if (active.Count < required)
        {
            throw new ConfigurationConflictException(
                $"нельзя {what}: на работе останется {active.Count} из {total}, а должно быть не меньше {required} "
                + $"({department.MinOnDutyPercent}% отдела; порог — в «Настройки → Рейтинг и сверхнорма»)");
        }

        var leaving = snapshot.Executors.GetValueOrDefault(executor.Id);
        if (leaving is null)
        {
            return;
        }

        var gaps = CoverageGaps(snapshot, leaving, active);
        if (gaps.Count > 0)
        {
            var shown = string.Join(", ", gaps.Take(MaxGapsShown)) + (gaps.Count > MaxGapsShown ? $" и ещё {gaps.Count - MaxGapsShown}" : "");
            throw new ConfigurationConflictException(
                $"нельзя {what}: {executor.FullName} — единственный на работе, кто может брать заявки «{shown}». "
                + "Сначала верните на работу коллегу с таким навыком");
        }
    }

    /// <summary>
    /// Виды заявок (значения справочников заявки), которые уходящий может брать, а из остающихся — никто.
    /// Проверка — по правилам на это поле, как в «Спросе и покрытии».
    /// </summary>
    public static List<string> CoverageGaps(BalancerSnapshot snapshot, ExecutorProfile leaving, IReadOnlyCollection<ExecutorProfile> staying)
    {
        var gaps = new List<string>();
        foreach (var field in snapshot.Catalog.All
                     .Where(f => f.Owner == FieldOwner.Order && f.Type == FieldType.Enum && f.Options.Length > 0)
                     .OrderBy(f => f.Id))
        {
            var rules = snapshot.Rules.Where(r => r.OrderField.Key == field.Key).ToList();
            if (rules.Count == 0)
            {
                continue;
            }

            foreach (var value in field.Options)
            {
                var probe = new Dictionary<string, FieldValue> { [field.Key] = FieldValue.FromText(value, FieldType.Enum) };
                bool Can(ExecutorProfile e) => rules.All(r => r.Check(probe, e.Values).Passed);
                if (Can(leaving) && !staying.Any(Can))
                {
                    gaps.Add($"{field.Label}: {field.Show(value)}");
                }
            }
        }

        return gaps;
    }

    private static IncomingExecutor Incoming(Executor e, bool isActive) =>
        new(e.Id, e.FullName, isActive, e.DailyLimit, e.QualificationWeight,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.AttributesJson) ?? []);

    private async Task AuditAsync(int departmentId, string action, Executor executor, CancellationToken cancellationToken)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            DepartmentId = departmentId,
            Actor = actor.Name,
            Action = action,
            Entity = "executor",
            EntityId = executor.Id.ToString(CultureInfo.InvariantCulture),
            DataJson = JsonSerializer.Serialize(new { after = new { executor.FullName } }, AuditJson),
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
