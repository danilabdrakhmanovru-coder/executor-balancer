using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Application.Configuration;

/// <summary>
/// Конструктор: параметры, правила подбора и правила веса. Каждое изменение проверяется
/// против справочника полей (типы, операторы, значения), пишется в журнал аудита вместе с состоянием
/// до и после и сразу применяется на всех экземплярах через версию конфигурации в Redis.
/// Изменения, которые сломали бы действующие правила или данные исполнителей, отклоняются.
/// </summary>
public sealed class ConfigurationService(
    IBalancerDbContext db,
    ILoadStore loadStore,
    ExecutorDirectory directory,
    IOptions<BalancerOptions> options,
    TimeProvider clock,
    ILogger<ConfigurationService> logger)
{
    public const int MaxFieldsPerOwner = FieldCatalog.MaxAttributes;
    public const int MaxRules = 200;
    public const int MaxWeightRules = 100;
    public const int MaxOptions = FieldValue.MaxItems;
    public const int MaxOptionLength = 100;
    public const int MaxLabelLength = 120;
    public const int MaxRuleNameLength = 160;
    public const int MaxPriority = 1_000_000;
    public const decimal MinWeight = 0.1m;
    public const decimal MaxWeight = 1000m;
    private const int MaxValueJsonLength = 8000;
    private const string Actor = "admin";

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<ConfigurationView> GetAsync(int departmentId, CancellationToken cancellationToken)
    {
        var fields = await db.FieldDefinitions.AsNoTracking()
            .Where(f => f.DepartmentId == departmentId)
            .OrderBy(f => f.Owner).ThenBy(f => f.Id)
            .ToListAsync(cancellationToken);
        var rules = await db.Rules.AsNoTracking()
            .Where(r => r.DepartmentId == departmentId)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        var weightRules = await db.WeightRules.AsNoTracking()
            .Where(r => r.DepartmentId == departmentId)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        var catalog = new FieldCatalog(fields);

        return new ConfigurationView(
            fields.Select(f => ToView(f, rules, weightRules)).ToList(),
            rules.Select(r => ToView(r, catalog)).ToList(),
            weightRules.Select(r => ToView(r, catalog)).ToList(),
            Enum.GetValues<RuleOperator>().Select(op => new OperatorView(op, CompiledRule.OperatorText(op))).ToList(),
            options.Value.DefaultOrderWeight,
            new ConfigurationLimits(MaxFieldsPerOwner, MaxRules, MaxWeightRules, MaxOptions, MaxOptionLength));
    }

    /// <summary>Журнал: изменения конфигурации и входы администратора, новые сверху.</summary>
    /// <remarks>Изменения этого отдела и общие события (входы, создание и удаление отделов).</remarks>
    public async Task<IReadOnlyList<AuditView>> GetAuditAsync(int departmentId, long? beforeId, int limit,
        CancellationToken cancellationToken)
    {
        var query = db.AuditEntries.AsNoTracking().Where(a => a.DepartmentId == departmentId || a.DepartmentId == null);
        if (beforeId is { } before)
        {
            query = query.Where(a => a.Id < before);
        }

        var rows = await query.OrderByDescending(a => a.Id).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
        return rows.Select(a => new AuditView(a.Id, a.Actor, a.Action, a.Entity, a.EntityId, Parse(a.DataJson), a.CreatedAt))
            .ToList();
    }

    // ---------- мотивация ----------

    public async Task<MotivationInput?> GetMotivationAsync(int departmentId, CancellationToken cancellationToken)
    {
        var d = await db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == departmentId, cancellationToken);
        return d is null ? null : ToInput(d);
    }

    /// <summary>Настройки рейтинга и режима «больше нормы» отдела. Проверка диапазонов, журнал, новая версия.</summary>
    public async Task<MotivationInput?> UpdateMotivationAsync(int departmentId, MotivationInput input,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.FastCloseSeconds is < 0 or > 86_400)
        {
            errors["fastCloseSeconds"] = ["от 0 (не проверять) до 86400 секунд"];
        }

        foreach (var (key, value) in new[]
                 {
                     ("reworkPenalty", input.ReworkPenalty), ("fastClosePenalty", input.FastClosePenalty),
                     ("qualityThreshold", input.QualityThreshold), ("heavyQualityThreshold", input.HeavyQualityThreshold),
                 })
        {
            if (value is < 0 or > 1)
            {
                errors[key] = ["доля от 0 до 1"];
            }
        }

        if (input.MaxExtraPercent is < 0 or > 100)
        {
            errors["maxExtraPercent"] = ["от 0 (режим выключен) до 100 процентов"];
        }

        if (input.HeavyQualityThreshold < input.QualityThreshold)
        {
            errors["heavyQualityThreshold"] = ["не ниже порога приостановки режима"];
        }

        if (input.HeavyWeight is < MinWeight or > MaxWeight)
        {
            errors["heavyWeight"] = [$"от {MinWeight} до {MaxWeight}"];
        }

        ThrowIfAny(errors);
        var department = await db.Departments.FirstOrDefaultAsync(x => x.Id == departmentId, cancellationToken);
        if (department is null)
        {
            return null;
        }

        var before = ToInput(department);
        department.FastCloseSeconds = input.FastCloseSeconds;
        department.ReworkPenalty = input.ReworkPenalty;
        department.FastClosePenalty = input.FastClosePenalty;
        department.MaxExtraPercent = input.MaxExtraPercent;
        department.QualityThreshold = input.QualityThreshold;
        department.HeavyQualityThreshold = input.HeavyQualityThreshold;
        department.HeavyWeight = input.HeavyWeight;
        await SaveWithAuditAsync(departmentId, "motivation_updated", "department", () => departmentId, before,
            ToInput(department), cancellationToken);
        return ToInput(department);
    }

    /// <summary>
    /// Режим «готов взять больше нормы» у сотрудника отдела: процент сверх суточного лимита, не выше потолка отдела.
    /// false — сотрудника в отделе нет.
    /// </summary>
    public async Task<bool> SetExtraModeAsync(int departmentId, long executorId, ExtraModeInput input,
        CancellationToken cancellationToken)
    {
        var department = await db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == departmentId, cancellationToken);
        var executor = await db.Executors.FirstOrDefaultAsync(e => e.Id == executorId && e.DepartmentId == departmentId,
            cancellationToken);
        if (department is null || executor is null)
        {
            return false;
        }

        if (input.Percent < 0 || input.Percent > department.MaxExtraPercent)
        {
            ThrowIfAny(new Dictionary<string, string[]>
            {
                ["percent"] = [$"от 0 до {department.MaxExtraPercent}% — потолок отдела"],
            });
        }

        if (input.Percent > 0 && executor.DailyLimit is null)
        {
            throw new ConfigurationConflictException(
                "у сотрудника нет суточного лимита — норма не задана, ему и так достаётся сколько распределится");
        }

        var before = new { executor.FullName, executor.ExtraPercent };
        executor.ExtraPercent = input.Percent;
        await SaveWithAuditAsync(departmentId, "extra_mode_changed", "executor", () => (int)Math.Min(executorId, int.MaxValue),
            before, new { executor.FullName, executor.ExtraPercent }, cancellationToken);
        return true;
    }

    private static MotivationInput ToInput(Department d) => new(d.FastCloseSeconds, d.ReworkPenalty, d.FastClosePenalty,
        d.MaxExtraPercent, d.QualityThreshold, d.HeavyQualityThreshold, d.HeavyWeight);

    // ---------- шаблоны сфер ----------

    /// <summary>Шаблоны и признак «сейчас применён»: все параметры шаблона есть в справочнике.</summary>
    public async Task<IReadOnlyList<PresetView>> GetPresetsAsync(int departmentId, CancellationToken cancellationToken)
    {
        // «применён» — все параметры шаблона есть с тем же типом и справочником
        var current = (await db.FieldDefinitions.AsNoTracking().Where(f => f.DepartmentId == departmentId).ToListAsync(cancellationToken))
            .ToDictionary(f => (f.Owner, f.Key));
        bool Same(PresetField f) => current.TryGetValue((f.Owner, f.Key), out var existing)
                                    && existing.Type == f.Type && existing.Options.SequenceEqual(f.Options);
        return DomainPresets.All
            .Select(p => new PresetView(p.Id, p.Title, p.Description,
                p.Fields.Where(f => f.Owner == FieldOwner.Order).Select(f => f.Label).ToArray(),
                p.Fields.Where(f => f.Owner == FieldOwner.Executor).Select(f => f.Label).ToArray(),
                p.Fields.All(Same)))
            .ToList();
    }

    /// <summary>
    /// Заменяет параметры, правила и веса на набор из шаблона — одной транзакцией и одной записью журнала.
    /// Заявки и исполнители не трогаются: их параметры хранятся целиком и начнут учитываться, когда появятся
    /// соответствующие поля.
    /// </summary>
    public async Task ApplyPresetAsync(int departmentId, string id, CancellationToken cancellationToken)
    {
        var preset = DomainPresets.Find(id)
                     ?? throw new InvalidInputException(new Dictionary<string, string[]> { ["preset"] = ["неизвестный шаблон"] });

        var department = await db.Departments.FirstOrDefaultAsync(d => d.Id == departmentId, cancellationToken)
                         ?? throw new InvalidInputException(new Dictionary<string, string[]> { ["department"] = ["отдел не найден"] });
        var previous = (await GetPresetsAsync(departmentId, cancellationToken)).FirstOrDefault(p => p.IsCurrent);
        var before = new
        {
            Preset = previous?.Title,
            Fields = await db.FieldDefinitions.AsNoTracking().Where(f => f.DepartmentId == departmentId)
                .OrderBy(f => f.Id).Select(f => f.Label).ToListAsync(cancellationToken),
            Rules = await db.Rules.AsNoTracking().Where(r => r.DepartmentId == departmentId)
                .OrderBy(r => r.Id).Select(r => r.Name).ToListAsync(cancellationToken),
        };
        var (fields, rules, weightRules) = DomainPresets.Build(preset, clock.GetUtcNow(), departmentId);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            db.Rules.RemoveRange(await db.Rules.Where(r => r.DepartmentId == departmentId).ToListAsync(cancellationToken));
            db.WeightRules.RemoveRange(await db.WeightRules.Where(r => r.DepartmentId == departmentId).ToListAsync(cancellationToken));
            db.FieldDefinitions.RemoveRange(await db.FieldDefinitions.Where(f => f.DepartmentId == departmentId).ToListAsync(cancellationToken));
            department.PresetId = preset.Id;
            department.SphereTitle = null;
            await db.SaveChangesAsync(cancellationToken);

            db.FieldDefinitions.AddRange(fields);
            db.Rules.AddRange(rules);
            db.WeightRules.AddRange(weightRules);
            db.AuditEntries.Add(new AuditEntry
            {
                DepartmentId = departmentId,
                Actor = Actor,
                Action = "preset_applied",
                Entity = "preset",
                EntityId = preset.Id,
                DataJson = JsonSerializer.Serialize(new
                {
                    before,
                    after = new { Preset = preset.Title, Fields = fields.Select(f => f.Label), Rules = rules.Select(r => r.Name) },
                }, AuditJson),
                CreatedAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await loadStore.BumpConfigVersionAsync(cancellationToken);
        directory.Invalidate();
        logger.LogInformation("Применён шаблон «{Preset}»", preset.Title);
    }

    // ---------- параметры ----------

    public async Task<FieldView> CreateFieldAsync(int departmentId, FieldInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var owner = input.Owner is { } o && Enum.IsDefined(o) ? o : Fail<FieldOwner>(errors, "owner", "Order или Executor");
        var key = input.Key?.Trim() ?? "";
        if (!FieldCatalog.IsValidKey(key))
        {
            errors["key"] = ["латинские строчные буквы, цифры и _, начинается с буквы, до 48 символов"];
        }

        var label = Label(input.Label, errors);
        var type = input.Type is { } t && Enum.IsDefined(t) ? t : Fail<FieldType>(errors, "type", "String, Number, Boolean, Enum или Array");
        var fieldOptions = NormalizeOptions(type, input.Options, errors);
        ThrowIfAny(errors);

        if (await db.FieldDefinitions.CountAsync(f => f.DepartmentId == departmentId && f.Owner == owner, cancellationToken) >= MaxFieldsPerOwner)
        {
            throw new ConfigurationConflictException($"не больше {MaxFieldsPerOwner} параметров у {OwnerText(owner)}");
        }

        if (await db.FieldDefinitions.AnyAsync(f => f.DepartmentId == departmentId && f.Owner == owner && f.Key == key, cancellationToken))
        {
            throw new ConfigurationConflictException($"у {OwnerText(owner)} уже есть параметр «{key}»");
        }

        var field = new FieldDefinition
        {
            DepartmentId = departmentId, Owner = owner, Key = key, Label = label, Type = type, Options = fieldOptions,
        };
        db.FieldDefinitions.Add(field);
        await SaveWithAuditAsync(departmentId, "field_created", "field", () => field.Id, null, Snapshot(field), cancellationToken);
        return ToView(field, [], []);
    }

    public async Task<FieldView?> UpdateFieldAsync(int departmentId, int id, FieldInput input,
        CancellationToken cancellationToken)
    {
        var field = await db.FieldDefinitions.FirstOrDefaultAsync(f => f.Id == id && f.DepartmentId == departmentId, cancellationToken);
        if (field is null)
        {
            return null;
        }

        var errors = new Dictionary<string, string[]>();
        if (input.Owner is { } owner && owner != field.Owner)
        {
            errors["owner"] = ["владельца параметра изменить нельзя"];
        }

        if (input.Key is { } key && key.Trim() != field.Key)
        {
            errors["key"] = ["ключ изменить нельзя: по нему АИС передаёт значения"];
        }

        // тип не меняется: АИС уже шлёт значения в старом формате, и заявки с ними начали бы отклоняться
        if (input.Type is { } type && type != field.Type)
        {
            errors["type"] = ["тип изменить нельзя: заведите новый параметр и перенесите на него правила"];
        }

        var label = Label(input.Label, errors);
        var fieldOptions = NormalizeOptions(field.Type, input.Options, errors);
        ThrowIfAny(errors);

        var before = Snapshot(field);
        var replacement = new FieldDefinition
        {
            Id = field.Id, DepartmentId = field.DepartmentId, Owner = field.Owner, Key = field.Key, Label = label, Type = field.Type, Options = fieldOptions,
        };
        await EnsureRulesSurviveAsync(field, replacement, cancellationToken);
        if (field.Owner == FieldOwner.Executor)
        {
            await EnsureExecutorValuesSurviveAsync(field, replacement, cancellationToken);
        }

        field.Label = label;
        field.Options = fieldOptions;
        await SaveWithAuditAsync(departmentId, "field_updated", "field", () => field.Id, before, Snapshot(field), cancellationToken);

        var rules = await db.Rules.AsNoTracking().Where(r => r.DepartmentId == departmentId).ToListAsync(cancellationToken);
        var weightRules = await db.WeightRules.AsNoTracking().Where(r => r.DepartmentId == departmentId).ToListAsync(cancellationToken);
        return ToView(field, rules, weightRules);
    }

    public async Task<bool> DeleteFieldAsync(int departmentId, int id, CancellationToken cancellationToken)
    {
        var field = await db.FieldDefinitions.FirstOrDefaultAsync(f => f.Id == id && f.DepartmentId == departmentId, cancellationToken);
        if (field is null)
        {
            return false;
        }

        await EnsureRulesSurviveAsync(field, replacement: null, cancellationToken);
        var before = Snapshot(field);
        db.FieldDefinitions.Remove(field);
        await SaveWithAuditAsync(departmentId, "field_deleted", "field", () => id, before, null, cancellationToken);
        return true;
    }

    // ---------- правила подбора ----------

    public async Task<RuleView> CreateRuleAsync(int departmentId, RuleInput input, CancellationToken cancellationToken)
    {
        if (await db.Rules.CountAsync(r => r.DepartmentId == departmentId, cancellationToken) >= MaxRules)
        {
            throw new ConfigurationConflictException($"не больше {MaxRules} правил");
        }

        var rule = new Rule { DepartmentId = departmentId };
        var catalog = await FillAsync(rule, input, cancellationToken);
        db.Rules.Add(rule);
        await SaveWithAuditAsync(departmentId, "rule_created", "rule", () => rule.Id, null, Snapshot(rule), cancellationToken);
        return ToView(rule, catalog);
    }

    public async Task<RuleView?> UpdateRuleAsync(int departmentId, int id, RuleInput input,
        CancellationToken cancellationToken)
    {
        var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id && r.DepartmentId == departmentId, cancellationToken);
        if (rule is null)
        {
            return null;
        }

        var before = Snapshot(rule);
        var catalog = await FillAsync(rule, input, cancellationToken);
        await SaveWithAuditAsync(departmentId, "rule_updated", "rule", () => rule.Id, before, Snapshot(rule), cancellationToken);
        return ToView(rule, catalog);
    }

    public async Task<bool> DeleteRuleAsync(int departmentId, int id, CancellationToken cancellationToken)
    {
        var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id && r.DepartmentId == departmentId, cancellationToken);
        if (rule is null)
        {
            return false;
        }

        var before = Snapshot(rule);
        db.Rules.Remove(rule);
        await SaveWithAuditAsync(departmentId, "rule_deleted", "rule", () => id, before, null, cancellationToken);
        return true;
    }

    // ---------- правила веса ----------

    public async Task<WeightRuleView> CreateWeightRuleAsync(int departmentId, WeightRuleInput input,
        CancellationToken cancellationToken)
    {
        if (await db.WeightRules.CountAsync(r => r.DepartmentId == departmentId, cancellationToken) >= MaxWeightRules)
        {
            throw new ConfigurationConflictException($"не больше {MaxWeightRules} правил веса");
        }

        var rule = new WeightRule { DepartmentId = departmentId };
        var catalog = await FillAsync(rule, input, cancellationToken);
        db.WeightRules.Add(rule);
        await SaveWithAuditAsync(departmentId, "weight_rule_created", "weight_rule", () => rule.Id, null, Snapshot(rule),
            cancellationToken);
        return ToView(rule, catalog);
    }

    public async Task<WeightRuleView?> UpdateWeightRuleAsync(int departmentId, int id, WeightRuleInput input,
        CancellationToken cancellationToken)
    {
        var rule = await db.WeightRules.FirstOrDefaultAsync(r => r.Id == id && r.DepartmentId == departmentId, cancellationToken);
        if (rule is null)
        {
            return null;
        }

        var before = Snapshot(rule);
        var catalog = await FillAsync(rule, input, cancellationToken);
        await SaveWithAuditAsync(departmentId, "weight_rule_updated", "weight_rule", () => rule.Id, before, Snapshot(rule),
            cancellationToken);
        return ToView(rule, catalog);
    }

    public async Task<bool> DeleteWeightRuleAsync(int departmentId, int id, CancellationToken cancellationToken)
    {
        var rule = await db.WeightRules.FirstOrDefaultAsync(r => r.Id == id && r.DepartmentId == departmentId, cancellationToken);
        if (rule is null)
        {
            return false;
        }

        var before = Snapshot(rule);
        db.WeightRules.Remove(rule);
        await SaveWithAuditAsync(departmentId, "weight_rule_deleted", "weight_rule", () => id, before, null, cancellationToken);
        return true;
    }

    // ---------- проверка и заполнение ----------

    private async Task<FieldCatalog> FillAsync(Rule rule, RuleInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = input.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxRuleNameLength)
        {
            errors["name"] = [$"обязательно, не длиннее {MaxRuleNameLength} символов"];
        }

        var priority = Priority(input.Priority, errors);
        var op = input.Operator is { } o && Enum.IsDefined(o) ? o : Fail<RuleOperator>(errors, "operator", "неизвестный оператор");
        var target = input.Target is { } t && Enum.IsDefined(t) ? t : Fail<RuleTarget>(errors, "target", "ExecutorField или Constant");
        var orderField = FieldKey(input.OrderField, "orderField", errors);
        string? executorField = null;
        string? executorFieldUpper = null;
        string? valueJson = null;
        if (target == RuleTarget.ExecutorField)
        {
            executorField = FieldKey(input.ExecutorField, "executorField", errors);
            if (op == RuleOperator.Between)
            {
                executorFieldUpper = FieldKey(input.ExecutorFieldUpper, "executorFieldUpper", errors);
            }
        }
        else
        {
            valueJson = ValueJson(input.Value, errors);
        }

        ThrowIfAny(errors);

        rule.Name = name;
        rule.IsEnabled = input.IsEnabled;
        rule.Priority = priority;
        rule.OrderField = orderField;
        rule.Operator = op;
        rule.Target = target;
        rule.ExecutorField = executorField;
        rule.ExecutorFieldUpper = executorFieldUpper;
        rule.ValueJson = valueJson;
        rule.IsStrict = input.IsStrict;
        rule.UpdatedAt = clock.GetUtcNow();

        var catalog = await CatalogAsync(rule.DepartmentId, cancellationToken);
        Compile(() => RuleCompiler.Compile(rule, catalog));
        return catalog;
    }

    private async Task<FieldCatalog> FillAsync(WeightRule rule, WeightRuleInput input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var priority = Priority(input.Priority, errors);
        var op = input.Operator is { } o && Enum.IsDefined(o) ? o : Fail<RuleOperator>(errors, "operator", "неизвестный оператор");
        var orderField = FieldKey(input.OrderField, "orderField", errors);
        var valueJson = ValueJson(input.Value, errors);
        if (input.Weight < MinWeight || input.Weight > MaxWeight || decimal.Round(input.Weight, 3) != input.Weight)
        {
            errors["weight"] = [$"от {MinWeight.ToString(CultureInfo.InvariantCulture)} до {MaxWeight.ToString(CultureInfo.InvariantCulture)}, не больше трёх знаков после точки"];
        }

        ThrowIfAny(errors);

        rule.IsEnabled = input.IsEnabled;
        rule.Priority = priority;
        rule.OrderField = orderField;
        rule.Operator = op;
        rule.ValueJson = valueJson!;
        rule.Weight = input.Weight;

        var catalog = await CatalogAsync(rule.DepartmentId, cancellationToken);
        Compile(() => RuleCompiler.CompileWeight(rule, catalog));
        return catalog;
    }

    /// <summary>Правила, которые работают сейчас, должны работать и после изменения параметра.</summary>
    private async Task EnsureRulesSurviveAsync(FieldDefinition current, FieldDefinition? replacement,
        CancellationToken cancellationToken)
    {
        var departmentId = current.DepartmentId;
        var fields = await db.FieldDefinitions.AsNoTracking().Where(f => f.DepartmentId == departmentId).ToListAsync(cancellationToken);
        var before = new FieldCatalog(fields);
        var after = new FieldCatalog(replacement is null
            ? fields.Where(f => f.Id != current.Id)
            : fields.Where(f => f.Id != current.Id).Append(replacement));

        var broken = new List<string>();
        foreach (var rule in await db.Rules.AsNoTracking().Where(r => r.DepartmentId == departmentId).OrderBy(r => r.Id).ToListAsync(cancellationToken))
        {
            if (Error(() => RuleCompiler.Compile(rule, before)) is null
                && Error(() => RuleCompiler.Compile(rule, after)) is { } reason)
            {
                broken.Add($"«{rule.Name}» ({reason})");
            }
        }

        foreach (var rule in await db.WeightRules.AsNoTracking().Where(r => r.DepartmentId == departmentId).OrderBy(r => r.Id).ToListAsync(cancellationToken))
        {
            if (Error(() => RuleCompiler.CompileWeight(rule, before)) is null
                && Error(() => RuleCompiler.CompileWeight(rule, after)) is { } reason)
            {
                broken.Add($"правило веса {rule.Weight.ToString(CultureInfo.InvariantCulture)} ({reason})");
            }
        }

        if (broken.Count > 0)
        {
            var what = replacement is null ? "Параметр используется в правилах" : "Изменение сломает правила";
            throw new ConfigurationConflictException($"{what}: {string.Join("; ", broken.Take(10))}. Сначала измените или удалите их.");
        }
    }

    /// <summary>
    /// Значения, которые АИС уже передала исполнителям, должны оставаться корректными. Иначе значение
    /// перестало бы разбираться, а нестрогое правило сочло бы его «не заданным» и сняло ограничение.
    /// </summary>
    private async Task EnsureExecutorValuesSurviveAsync(FieldDefinition current, FieldDefinition replacement,
        CancellationToken cancellationToken)
    {
        var executors = await db.Executors.AsNoTracking()
            .Where(e => e.DepartmentId == current.DepartmentId)
            .Select(e => new { e.Id, e.FullName, e.AttributesJson })
            .ToListAsync(cancellationToken);
        var broken = new List<string>();
        foreach (var executor in executors)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(executor.AttributesJson);
            if (values is null || !values.TryGetValue(current.Key, out var raw))
            {
                continue;
            }

            if (FieldValue.TryParse(raw, current.Type, current.Options, out _, out _)
                && !FieldValue.TryParse(raw, replacement.Type, replacement.Options, out _, out var error))
            {
                broken.Add($"{executor.FullName} (#{executor.Id}): {error}");
            }
        }

        if (broken.Count > 0)
        {
            throw new ConfigurationConflictException(
                $"У исполнителей есть значения, которые станут некорректными: {string.Join("; ", broken.Take(5))}" +
                (broken.Count > 5 ? $" и ещё {broken.Count - 5}" : "") + ". Сначала обновите их в АИС.");
        }
    }

    private async Task SaveWithAuditAsync(int departmentId, string action, string entity, Func<int> entityId, object? before,
        object? after, CancellationToken cancellationToken)
    {
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
            {
                throw new ConfigurationConflictException("такая запись уже есть");
            }

            db.AuditEntries.Add(new AuditEntry
            {
                DepartmentId = departmentId,
                Actor = Actor,
                Action = action,
                Entity = entity,
                EntityId = entityId().ToString(CultureInfo.InvariantCulture),
                DataJson = JsonSerializer.Serialize(new { before, after }, AuditJson),
                CreatedAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // новая версия конфигурации: остальные экземпляры перечитают её в течение ConfigCheckInterval
        await loadStore.BumpConfigVersionAsync(cancellationToken);
        directory.Invalidate();
        logger.LogInformation("Конфигурация изменена: {Action} {Entity} #{EntityId}", action, entity, entityId());
    }

    private async Task<FieldCatalog> CatalogAsync(int departmentId, CancellationToken cancellationToken) =>
        new(await db.FieldDefinitions.AsNoTracking().Where(f => f.DepartmentId == departmentId).ToListAsync(cancellationToken));

    private static void Compile(Action compile)
    {
        if (Error(compile) is { } error)
        {
            throw new InvalidInputException(new Dictionary<string, string[]> { ["rule"] = [error] });
        }
    }

    private static string? Error(Action compile)
    {
        try
        {
            compile();
            return null;
        }
        catch (RuleValidationException ex)
        {
            return ex.Message;
        }
    }

    private static string Label(string? value, Dictionary<string, string[]> errors)
    {
        var label = value?.Trim() ?? "";
        if (label.Length is 0 or > MaxLabelLength)
        {
            errors["label"] = [$"обязательно, не длиннее {MaxLabelLength} символов"];
        }

        return label;
    }

    private static string[] NormalizeOptions(FieldType type, string[]? values, Dictionary<string, string[]> errors)
    {
        var list = (values ?? []).Select(v => v?.Trim() ?? "").Where(v => v.Length > 0).ToList();
        if (type is not (FieldType.Enum or FieldType.Array))
        {
            if (list.Count > 0)
            {
                errors["options"] = ["справочник значений бывает только у типов Enum и Array"];
            }

            return [];
        }

        if (list.Count > MaxOptions)
        {
            errors["options"] = [$"не больше {MaxOptions} значений"];
        }
        else if (list.Any(v => v.Length > MaxOptionLength))
        {
            errors["options"] = [$"значение справочника не длиннее {MaxOptionLength} символов"];
        }
        else if (list.Distinct(StringComparer.Ordinal).Count() != list.Count)
        {
            errors["options"] = ["значения справочника повторяются"];
        }

        return list.ToArray();
    }

    private static int Priority(int value, Dictionary<string, string[]> errors)
    {
        if (value is < 0 or > MaxPriority)
        {
            errors["priority"] = [$"от 0 до {MaxPriority}"];
        }

        return value;
    }

    private static string FieldKey(string? value, string name, Dictionary<string, string[]> errors)
    {
        var key = value?.Trim() ?? "";
        if (!FieldCatalog.IsValidKey(key))
        {
            errors[name] = ["выберите параметр"];
        }

        return key;
    }

    private static string? ValueJson(JsonElement? value, Dictionary<string, string[]> errors)
    {
        if (value is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element)
        {
            errors["value"] = ["укажите значение для сравнения"];
            return null;
        }

        var json = element.GetRawText();
        if (json.Length > MaxValueJsonLength)
        {
            errors["value"] = [$"значение длиннее {MaxValueJsonLength} символов"];
        }

        return json;
    }

    private static T Fail<T>(Dictionary<string, string[]> errors, string key, string message)
        where T : struct
    {
        errors[key] = [message];
        return default;
    }

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new InvalidInputException(errors);
        }
    }

    private static string OwnerText(FieldOwner owner) => owner == FieldOwner.Order ? "заявки" : "исполнителя";

    // ---------- представление ----------

    private static FieldView ToView(FieldDefinition field, IReadOnlyList<Rule> rules, IReadOnlyList<WeightRule> weightRules)
    {
        var used = field.Owner == FieldOwner.Order
            ? rules.Count(r => r.OrderField == field.Key) + weightRules.Count(r => r.OrderField == field.Key)
            : rules.Count(r => r.Target == RuleTarget.ExecutorField
                               && (r.ExecutorField == field.Key || r.ExecutorFieldUpper == field.Key));
        var hint = DomainPresets.Hint(field.Owner, field.Key);
        return new FieldView(field.Id, field.Owner, field.Key, field.Label, field.Type, field.Options, used,
            hint?.Min ?? hint?.Choices?.Min(), hint?.Max ?? hint?.Choices?.Max());
    }

    private static RuleView ToView(Rule rule, FieldCatalog catalog) => new(
        rule.Id,
        rule.Name,
        rule.IsEnabled,
        rule.Priority,
        rule.OrderField,
        rule.Operator,
        rule.Target,
        rule.ExecutorField,
        rule.ExecutorFieldUpper,
        Parse(rule.ValueJson),
        rule.IsStrict,
        Describe(rule, catalog),
        Error(() => RuleCompiler.Compile(rule, catalog)),
        rule.UpdatedAt);

    private static WeightRuleView ToView(WeightRule rule, FieldCatalog catalog) => new(
        rule.Id,
        rule.IsEnabled,
        rule.Priority,
        rule.OrderField,
        rule.Operator,
        Parse(rule.ValueJson),
        rule.Weight,
        $"{FieldLabel(catalog, FieldOwner.Order, rule.OrderField)} {CompiledRule.OperatorText(rule.Operator)} {ValueText(rule.ValueJson)}",
        Error(() => RuleCompiler.CompileWeight(rule, catalog)));

    /// <summary>Правило человеческим языком — для списка в конструкторе и журнала.</summary>
    public static string Describe(Rule rule, FieldCatalog catalog)
    {
        var left = FieldLabel(catalog, FieldOwner.Order, rule.OrderField);
        var op = CompiledRule.OperatorText(rule.Operator);
        if (rule.Target == RuleTarget.Constant)
        {
            return $"{left} {op} {ValueText(rule.ValueJson)}";
        }

        var right = FieldLabel(catalog, FieldOwner.Executor, rule.ExecutorField);
        return rule.Operator == RuleOperator.Between
            ? $"{left} {op} [{right}; {FieldLabel(catalog, FieldOwner.Executor, rule.ExecutorFieldUpper)}] исполнителя"
            : $"{left} {op} «{right}» исполнителя";
    }

    private static string FieldLabel(FieldCatalog catalog, FieldOwner owner, string? key) =>
        catalog.Find(owner, key)?.Label ?? $"«{key}» (нет в справочнике)";

    private static string ValueText(string? json)
    {
        if (Parse(json) is not { } value)
        {
            return "—";
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(Scalar)) + "]",
            _ => Scalar(value),
        };

        static string Scalar(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.True => "да",
            JsonValueKind.False => "нет",
            _ => element.GetRawText(),
        };
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object Snapshot(FieldDefinition f) => new { f.Owner, f.Key, f.Label, f.Type, f.Options };

    private static object Snapshot(Rule r) => new
    {
        r.Name, r.IsEnabled, r.Priority, r.OrderField, r.Operator, r.Target, r.ExecutorField, r.ExecutorFieldUpper,
        Value = Parse(r.ValueJson), r.IsStrict,
    };

    private static object Snapshot(WeightRule r) => new
    {
        r.IsEnabled, r.Priority, r.OrderField, r.Operator, Value = Parse(r.ValueJson), r.Weight,
    };
}
