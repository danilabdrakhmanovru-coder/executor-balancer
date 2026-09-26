using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ExecutorBalancer.Infrastructure.Persistence;

/// <summary>Для dotnet ef: строка подключения берётся из переменной окружения.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BalancerDbContext>
{
    public BalancerDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
                         ?? "Host=localhost;Port=5432;Database=balancer;Username=balancer;Password=design-time";
        var options = new DbContextOptionsBuilder<BalancerDbContext>().UseNpgsql(connection).Options;
        return new BalancerDbContext(options);
    }
}
