using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Infrastructure.Persistence;

/// <summary>
/// Стартовые отделы — по одному на каждый шаблон сферы: «Банк» (пример из кейса), «Колл-центр»,
/// «Логистика», «Интернет-магазин». Пишутся только в пустую базу; в базе, обновлённой со старой версии
/// (один «Основной отдел» со своей настройкой), отдел сохраняется, а недостающие сферы добавляются рядом.
/// </summary>
internal static class DefaultConfiguration
{
    private static readonly (string PresetId, string Name)[] Seeds =
    [
        (DomainPresets.Bank.Id, "Банк"),
        (DomainPresets.Support.Id, "Колл-центр"),
        (DomainPresets.Logistics.Id, "Логистика"),
        (DomainPresets.Ecommerce.Id, "Интернет-магазин"),
    ];

    public static async Task SeedAsync(BalancerDbContext db, CancellationToken cancellationToken)
    {
        var departments = await db.Departments.ToListAsync(cancellationToken);
        if (departments.Count > 1)
        {
            return;
        }

        var main = departments.SingleOrDefault();
        if (main is not null && main.Code != "main")
        {
            return; // отдел уже настроен
        }

        var now = DateTimeOffset.UtcNow;
        if (main is null)
        {
            // база создана без миграций (тесты на SQLite): основной отдел создаём сами
            main = new Department { Id = Department.DefaultId, Code = "main", Name = "", CreatedAt = now };
            db.Departments.Add(main);
        }

        // сфера основного отдела: пустой — «Банк», уже настроенный — шаблон, чьи параметры совпадают
        var keys = await db.FieldDefinitions.Where(f => f.DepartmentId == main.Id).Select(f => f.Key).ToListAsync(cancellationToken);
        var mainPreset = keys.Count == 0
            ? DomainPresets.Bank
            : DomainPresets.All.FirstOrDefault(p => p.Fields.All(f => keys.Contains(f.Key)));
        var mainSeed = Seeds.FirstOrDefault(s => s.PresetId == mainPreset?.Id);
        main.Code = mainSeed.PresetId ?? "main";
        main.Name = mainSeed.Name ?? "Основной отдел";
        main.PresetId = mainPreset?.Id;
        if (keys.Count == 0)
        {
            Add(db, DomainPresets.Bank, main.Id, now);
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var (presetId, name) in Seeds.Where(s => s.PresetId != main.PresetId))
        {
            var department = new Department { Code = presetId, Name = name, PresetId = presetId, CreatedAt = now };
            db.Departments.Add(department);
            await db.SaveChangesAsync(cancellationToken);
            Add(db, DomainPresets.Find(presetId)!, department.Id, now);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static void Add(BalancerDbContext db, DomainPreset preset, int departmentId, DateTimeOffset now)
    {
        var (fields, rules, weightRules) = DomainPresets.Build(preset, now, departmentId);
        db.FieldDefinitions.AddRange(fields);
        db.Rules.AddRange(rules);
        db.WeightRules.AddRange(weightRules);
    }
}
