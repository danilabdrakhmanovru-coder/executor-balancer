using System.Text;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Tests;

/// <summary>
/// Состав смены: на перерыв и в увольнение уходят, только пока на работе остаётся доля отдела и кто-то для каждого
/// вида заявок. Уволенный пропадает из отдела, его открытые заявки уходят коллегам. Без навыков сотрудника не завести.
/// </summary>
public class StaffTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<bool?> Break(long id, bool active = false) =>
        _f.Staff(s => s.SetActiveAsync(D, id, active, CancellationToken.None));

    private async Task SetMinOnDuty(int percent)
    {
        var current = (await _f.Config(c => c.GetMotivationAsync(D, CancellationToken.None)))!;
        await _f.Config(c => c.UpdateMotivationAsync(D, current with { MinOnDutyPercent = percent }, CancellationToken.None));
    }

    [Fact]
    public async Task ShareOfDepartmentStaysOnDuty()
    {
        for (var id = 1; id <= 4; id++)
        {
            await _f.AddExecutor(id);
        }

        // 30% от четырёх — не меньше двух на работе
        Assert.Equal(false, await Break(1));
        Assert.Equal(false, await Break(2));
        var error = await Assert.ThrowsAsync<ConfigurationConflictException>(() => Break(3));
        Assert.Contains("не меньше 2", error.Message, StringComparison.Ordinal);

        Assert.Equal(true, await Break(1, active: true)); // вернуться можно всегда
        Assert.Equal(false, await Break(3));
    }

    [Fact]
    public async Task SomeoneStaysForEveryKindOfOrder()
    {
        await SetMinOnDuty(0);
        await _f.AddExecutor(1);                          // умеет все тематики
        await _f.AddExecutor(2, subjects: ["кредит"]);   // только кредиты

        var error = await Assert.ThrowsAsync<ConfigurationConflictException>(() => Break(1));
        Assert.Contains("Тематика: вклад", error.Message, StringComparison.Ordinal);
        Assert.Equal(false, await Break(2)); // кредиты остаются за первым
    }

    [Fact]
    public async Task DismissedExecutorLeavesOrdersToColleagues()
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);
        await _f.AddExecutor(3);
        await _f.Receive(1);
        var owner = (await _f.Query(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == 1))).ExecutorId!.Value;

        Assert.True(await _f.Staff(s => s.DismissAsync(D, owner, CancellationToken.None)));

        var order = await _f.Query(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == 1));
        Assert.NotNull(order.ExecutorId);
        Assert.NotEqual(owner, order.ExecutorId);
        Assert.False(await _f.Query(db => db.Executors.AnyAsync(e => e.Id == owner)));
        Assert.True(await _f.Query(db => db.AuditEntries.AnyAsync(a => a.Action == "executor_dismissed")));
        Assert.False(await _f.Staff(s => s.DismissAsync(D, owner, CancellationToken.None))); // уже нет
    }

    [Fact]
    public async Task LastExecutorCannotBeDismissed()
    {
        await SetMinOnDuty(0);
        await _f.AddExecutor(1);

        await Assert.ThrowsAsync<ConfigurationConflictException>(() => _f.Staff(s => s.DismissAsync(D, 1, CancellationToken.None)));
    }

    [Fact]
    public async Task ImportRequiresEveryParameter()
    {
        var header = "ФИО;Тематики;Типы заявок;Сегменты клиентов;Категории клиентов;Минимальная сумма;Максимальная сумма";
        var file = Encoding.UTF8.GetBytes(header + "\n"
            + "Пустые тематики;;ORDER_1;малый;обычный;0;500000\n"
            + "Всё заполнено;кредит;ORDER_1;малый;обычный;0;500000\n");

        var preview = await _f.Import(i => i.PreviewAsync(D, file, CancellationToken.None));

        var error = Assert.Single(preview.Rows[0].Errors);
        Assert.StartsWith("не заполнено: Тематики —", error, StringComparison.Ordinal);
        Assert.Empty(preview.Rows[1].Errors);
    }
}
