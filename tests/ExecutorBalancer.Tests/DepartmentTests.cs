using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Tests;

/// <summary>Отделы — независимые пространства: свои параметры, правила, исполнители, заявки и отчёты.</summary>
public class DepartmentTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<int> DepartmentId(string code) =>
        _f.Departments(async d => (await d.FindByCodeAsync(code, CancellationToken.None))!.Value);

    [Fact]
    public async Task SeedCreatesDepartmentPerSphere()
    {
        var departments = await _f.Departments(d => d.ListAsync(CancellationToken.None));

        Assert.Equal(["bank", "support", "logistics", "ecommerce"], departments.Select(d => d.Code));
        Assert.Equal(D, departments[0].Id);
        Assert.Equal("Банк", departments[0].Name);
        Assert.All(departments, d => Assert.Equal(d.Code, d.PresetId));

        // у каждого отдела — параметры своей сферы
        var logistics = await _f.Config(c => c.GetAsync(departments[2].Id, CancellationToken.None));
        Assert.Contains(logistics.Fields, f => f.Key == "region");
        Assert.DoesNotContain(logistics.Fields, f => f.Key == "subject");
    }

    [Fact]
    public async Task OrderGoesOnlyToExecutorsOfItsDepartment()
    {
        var logistics = await DepartmentId("logistics");
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);
        // тот же профиль, но исполнитель переведён в другой отдел
        await _f.Run(async b =>
        {
            await b.UpsertExecutorAsync(logistics, new IncomingExecutor(2, "Исполнитель 2", true, null, 1m,
                BalancerFixture.Attributes(new { regions = new[] { "Уфа" } })), CancellationToken.None);
            return true;
        });

        for (var i = 1; i <= 6; i++)
        {
            Assert.Equal(1, (await _f.Receive(i)).ExecutorId);
        }

        var report = await _f.Analytics();
        Assert.Equal([1L], report.Executors.Select(e => e.Id));
        var logisticsReport = await _f.Analytics(department: logistics);
        Assert.Equal(0, logisticsReport.Executors.Sum(e => e.Assigned));
    }

    [Fact]
    public async Task MovingExecutorReassignsItsOpenOrders()
    {
        var support = await DepartmentId("support");
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);
        var first = await _f.Receive(1);
        var moving = first.ExecutorId!.Value;

        await _f.Run(async b =>
        {
            await b.UpsertExecutorAsync(support, new IncomingExecutor(moving, "Переведён", true, null, 1m,
                BalancerFixture.Attributes(new { })), CancellationToken.None);
            return true;
        });

        var order = await _f.Query(db => db.Orders.AsNoTracking().FirstAsync(o => o.Id == 1));
        Assert.Equal(3 - moving, order.ExecutorId);
        Assert.Equal(D, order.DepartmentId);
    }

    [Fact]
    public async Task ConfigurationChangeStaysInItsDepartment()
    {
        var support = await DepartmentId("support");
        var before = await _f.Config(c => c.GetAsync(D, CancellationToken.None));

        await _f.Config(c => c.CreateFieldAsync(support,
            new FieldInput(FieldOwner.Order, "vip_line", "Выделенная линия", FieldType.Boolean, null), CancellationToken.None));
        await _f.Config(async c =>
        {
            await c.ApplyPresetAsync(support, DomainPresets.Logistics.Id, CancellationToken.None);
            return true;
        });

        var after = await _f.Config(c => c.GetAsync(D, CancellationToken.None));
        Assert.Equal(before.Fields.Select(f => f.Key), after.Fields.Select(f => f.Key));
        Assert.Equal(before.Rules.Select(r => r.Id), after.Rules.Select(r => r.Id));

        // правка чужого параметра через другой отдел не находит запись
        var foreign = before.Fields[0].Id;
        Assert.False(await _f.Config(c => c.DeleteFieldAsync(support, foreign, CancellationToken.None)));
    }

    [Fact]
    public async Task CreateWithPresetCopiesSphereConfiguration()
    {
        var created = await _f.Departments(d => d.CreateAsync(
            new DepartmentInput("Склад Казань", null, DomainPresets.Logistics.Id), CancellationToken.None));

        Assert.Equal("dept5", created.Code); // код шаблона уже занят отделом «Логистика»
        var config = await _f.Config(c => c.GetAsync(created.Id, CancellationToken.None));
        Assert.Equal(DomainPresets.Logistics.Fields.Count, config.Fields.Count);
        Assert.All(config.Rules, r => Assert.Null(r.Error));

        var audit = await _f.Config(c => c.GetAuditAsync(D, null, 10, CancellationToken.None));
        Assert.Contains(audit, a => a.Action == "department_created");
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("Отдел", "Bad Code")]
    [InlineData("Отдел", "1abc")]
    [InlineData("Отдел", "../admin")]
    public async Task InvalidDepartmentIsRejected(string name, string? code) =>
        await Assert.ThrowsAsync<InvalidInputException>(() =>
            _f.Departments(d => d.CreateAsync(new DepartmentInput(name, code, null), CancellationToken.None)));

    [Fact]
    public async Task DuplicateNameOrCodeIsConflict()
    {
        await Assert.ThrowsAsync<ConfigurationConflictException>(() =>
            _f.Departments(d => d.CreateAsync(new DepartmentInput("банк", null, null), CancellationToken.None)));
        await Assert.ThrowsAsync<ConfigurationConflictException>(() =>
            _f.Departments(d => d.CreateAsync(new DepartmentInput("Другой", "support", null), CancellationToken.None)));
    }

    [Fact]
    public async Task OnlyEmptyNonDefaultDepartmentCanBeDeleted()
    {
        var logistics = await DepartmentId("logistics");
        await _f.Run(async b =>
        {
            await b.UpsertExecutorAsync(logistics, new IncomingExecutor(7, "Кладовщик", true, null, 1m,
                BalancerFixture.Attributes(new { })), CancellationToken.None);
            return true;
        });

        await Assert.ThrowsAsync<ConfigurationConflictException>(() =>
            _f.Departments(d => d.DeleteAsync(D, CancellationToken.None)));
        await Assert.ThrowsAsync<ConfigurationConflictException>(() =>
            _f.Departments(d => d.DeleteAsync(logistics, CancellationToken.None)));

        var ecommerce = await DepartmentId("ecommerce");
        Assert.True(await _f.Departments(d => d.DeleteAsync(ecommerce, CancellationToken.None)));
        Assert.False(await _f.Departments(d => d.ExistsAsync(ecommerce, CancellationToken.None)));
        Assert.Equal(0, await _f.Query(db => db.FieldDefinitions.CountAsync(f => f.DepartmentId == ecommerce)));
    }
}
