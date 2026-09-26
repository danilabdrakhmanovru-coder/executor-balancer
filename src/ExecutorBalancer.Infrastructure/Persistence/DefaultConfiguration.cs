using ExecutorBalancer.Application.Configuration;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Infrastructure.Persistence;

/// <summary>Стартовая конфигурация — шаблон «Банк» из примера кейса. Пишется только в пустую базу.</summary>
internal static class DefaultConfiguration
{
    public static async Task SeedAsync(BalancerDbContext db, CancellationToken cancellationToken)
    {
        if (await db.FieldDefinitions.AnyAsync(cancellationToken))
        {
            return;
        }

        var (fields, rules, weightRules) = DomainPresets.Build(DomainPresets.Bank, DateTimeOffset.UtcNow);
        db.FieldDefinitions.AddRange(fields);
        db.Rules.AddRange(rules);
        db.WeightRules.AddRange(weightRules);
        await db.SaveChangesAsync(cancellationToken);
    }
}
