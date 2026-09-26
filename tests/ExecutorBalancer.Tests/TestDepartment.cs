global using static ExecutorBalancer.Tests.TestDepartment;

namespace ExecutorBalancer.Tests;

/// <summary>Отдел, в котором работают тесты: основной (он же «Банк» после стартового заполнения).</summary>
public static class TestDepartment
{
    public const int D = ExecutorBalancer.Domain.Department.DefaultId;
}
