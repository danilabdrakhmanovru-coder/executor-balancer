using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExecutorBalancer.Application.Configuration;

/// <param name="Sphere">Своя сфера из мастера — вместо шаблона.</param>
public sealed record DepartmentInput(string? Name, string? Code, string? PresetId, SphereInput? Sphere = null);

public sealed record DepartmentView(int Id, string Code, string Name, string? PresetId, string? PresetTitle,
    int Executors, int ActiveExecutors, int OpenOrders, DateTimeOffset CreatedAt);

/// <summary>
/// Отделы — независимые пространства: у каждого свои параметры, правила, исполнители, заявки и отчёты.
/// Сфера (шаблон) выбирается при создании отдела; другие отделы при этом не меняются.
/// </summary>
public sealed partial class DepartmentService(
    IBalancerDbContext db,
    ILoadStore loadStore,
    ExecutorDirectory directory,
    TimeProvider clock,
    ILogger<DepartmentService> logger,
    Users.ICurrentActor actor)
{
    public const int MaxDepartments = 20;
    public const int MaxNameLength = 120;

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")]
    private static partial Regex CodePattern();

    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code);

    public async Task<IReadOnlyList<DepartmentView>> ListAsync(CancellationToken cancellationToken)
    {
        var departments = await db.Departments.AsNoTracking().OrderBy(d => d.Id).ToListAsync(cancellationToken);
        var executors = await db.Executors.AsNoTracking()
            .GroupBy(e => e.DepartmentId)
            .Select(g => new { Id = g.Key, Total = g.Count(), Active = g.Count(e => e.IsActive) })
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var open = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Processed || o.Status == OrderStatus.Await)
            .GroupBy(o => o.DepartmentId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);

        return departments.Select(d =>
        {
            executors.TryGetValue(d.Id, out var e);
            return new DepartmentView(d.Id, d.Code, d.Name, d.PresetId, DomainPresets.Find(d.PresetId)?.Title ?? d.SphereTitle,
                e?.Total ?? 0, e?.Active ?? 0, open.GetValueOrDefault(d.Id), d.CreatedAt);
        }).ToList();
    }

    public Task<bool> ExistsAsync(int id, CancellationToken cancellationToken) =>
        db.Departments.AsNoTracking().AnyAsync(d => d.Id == id, cancellationToken);

    public async Task<int?> FindByCodeAsync(string code, CancellationToken cancellationToken) =>
        IsValidCode(code)
            ? await db.Departments.AsNoTracking().Where(d => d.Code == code).Select(d => (int?)d.Id).FirstOrDefaultAsync(cancellationToken)
            : null;

    /// <summary>Новый отдел; если указан шаблон — сразу с его параметрами, правилами и весами.</summary>
    public async Task<DepartmentView> CreateAsync(DepartmentInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = Name(input.Name, errors);
        DomainPreset? preset = null;
        if (!string.IsNullOrWhiteSpace(input.PresetId) && (preset = DomainPresets.Find(input.PresetId)) is null)
        {
            errors["presetId"] = ["неизвестный шаблон"];
        }

        if (preset is not null && input.Sphere is not null)
        {
            errors["sphere"] = ["либо шаблон, либо своя сфера"];
        }

        var code = input.Code?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(code) && !IsValidCode(code))
        {
            errors["code"] = ["код: латинские буквы, цифры, «-» и «_», начинается с буквы, до 32 символов"];
        }

        ThrowIfAny(errors);

        var existing = await db.Departments.AsNoTracking().Select(d => new { d.Code, d.Name }).ToListAsync(cancellationToken);
        if (existing.Count >= MaxDepartments)
        {
            throw new ConfigurationConflictException($"отделов не больше {MaxDepartments}");
        }

        if (existing.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConfigurationConflictException("отдел с таким названием уже есть");
        }

        if (string.IsNullOrEmpty(code))
        {
            // код для адреса API: от шаблона, иначе dept2, dept3…
            var codes = existing.Select(d => d.Code).ToHashSet();
            code = preset is not null && !codes.Contains(preset.Id) ? preset.Id : null;
            for (var i = existing.Count + 1; code is null; i++)
            {
                code = codes.Contains($"dept{i}") ? null : $"dept{i}";
            }
        }
        else if (existing.Any(d => d.Code == code))
        {
            throw new ConfigurationConflictException("отдел с таким кодом уже есть");
        }

        var now = clock.GetUtcNow();
        // своя сфера проверяется целиком до того, как что-либо записано
        var sphere = input.Sphere is { } custom ? SphereBuilder.Build(custom, 0, now) : null;
        var department = new Department
        {
            Code = code, Name = name, PresetId = preset?.Id, SphereTitle = sphere?.Title, CreatedAt = now,
        };
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            db.Departments.Add(department);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
            {
                throw new ConfigurationConflictException("отдел с таким кодом уже есть");
            }

            if (preset is not null)
            {
                var (fields, rules, weightRules) = DomainPresets.Build(preset, now, department.Id);
                db.FieldDefinitions.AddRange(fields);
                db.Rules.AddRange(rules);
                db.WeightRules.AddRange(weightRules);
            }
            else if (sphere is not null)
            {
                sphere.Fields.ForEach(f => f.DepartmentId = department.Id);
                sphere.Rules.ForEach(r => r.DepartmentId = department.Id);
                sphere.WeightRules.ForEach(w => w.DepartmentId = department.Id);
                db.FieldDefinitions.AddRange(sphere.Fields);
                db.Rules.AddRange(sphere.Rules);
                db.WeightRules.AddRange(sphere.WeightRules);
            }

            Audit("department_created", department.Id, null, Snapshot(department));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await ChangedAsync(cancellationToken);
        logger.LogInformation("Создан отдел «{Name}» ({Code})", department.Name, department.Code);
        return (await ListAsync(cancellationToken)).First(d => d.Id == department.Id);
    }

    public async Task<bool> RenameAsync(int id, DepartmentInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = Name(input.Name, errors);
        ThrowIfAny(errors);

        var department = await db.Departments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (department is null)
        {
            return false;
        }

        var names = await db.Departments.AsNoTracking().Where(d => d.Id != id).Select(d => d.Name).ToListAsync(cancellationToken);
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConfigurationConflictException("отдел с таким названием уже есть");
        }

        var before = Snapshot(department);
        department.Name = name;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
            Audit("department_renamed", id, before, Snapshot(department));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Удаляет пустой отдел вместе с его настройками. Отдел с сотрудниками или заявками не удаляется:
    /// история распределения должна сохраниться. Основной отдел принимает заявки по старым адресам API.
    /// </summary>
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var department = await db.Departments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (department is null)
        {
            return false;
        }

        if (id == Department.DefaultId)
        {
            throw new ConfigurationConflictException("основной отдел удалить нельзя: на него приходят заявки по общему адресу API");
        }

        if (await db.Executors.AnyAsync(e => e.DepartmentId == id, cancellationToken)
            || await db.Orders.AnyAsync(o => o.DepartmentId == id, cancellationToken))
        {
            throw new ConfigurationConflictException("в отделе есть сотрудники или заявки — удалить можно только пустой отдел");
        }

        var before = Snapshot(department);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            db.Rules.RemoveRange(await db.Rules.Where(r => r.DepartmentId == id).ToListAsync(cancellationToken));
            db.WeightRules.RemoveRange(await db.WeightRules.Where(r => r.DepartmentId == id).ToListAsync(cancellationToken));
            db.FieldDefinitions.RemoveRange(await db.FieldDefinitions.Where(f => f.DepartmentId == id).ToListAsync(cancellationToken));
            db.Departments.Remove(department);
            Audit("department_deleted", id, before, null);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await ChangedAsync(cancellationToken);
        logger.LogInformation("Удалён отдел «{Name}»", department.Name);
        return true;
    }

    private void Audit(string action, int id, object? before, object? after) =>
        // общее событие (DepartmentId = null): видно в журнале любого отдела
        db.AuditEntries.Add(new AuditEntry
        {
            Actor = actor.Name,
            Action = action,
            Entity = "department",
            EntityId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataJson = JsonSerializer.Serialize(new { before, after }, AuditJson),
            CreatedAt = clock.GetUtcNow(),
        });

    private async Task ChangedAsync(CancellationToken cancellationToken)
    {
        await loadStore.BumpConfigVersionAsync(cancellationToken);
        directory.Invalidate();
    }

    private static object Snapshot(Department d) => new { d.Code, d.Name, Preset = DomainPresets.Find(d.PresetId)?.Title ?? d.SphereTitle };

    private static string Name(string? value, Dictionary<string, string[]> errors)
    {
        var name = value?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            errors["name"] = [$"название отдела: от 1 до {MaxNameLength} символов"];
        }

        return name;
    }

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new InvalidInputException(errors);
        }
    }
}
