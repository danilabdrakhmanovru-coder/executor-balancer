using System.Text;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Executors;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Tests;

/// <summary>Загрузка сотрудников из CSV: шаблон, проверка по справочнику, запись одной транзакцией.</summary>
public class ExecutorImportTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private static byte[] Utf8(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];

    private Task<ImportPreview> Preview(byte[] file) => _f.Import(i => i.PreviewAsync(D, file, CancellationToken.None));

    private Task<ImportPreview> Apply(byte[] file) => _f.Import(i => i.ApplyAsync(D, file, CancellationToken.None));

    [Fact]
    public async Task TemplateHasDepartmentParameters()
    {
        var bytes = await _f.Import(i => i.TemplateAsync(D, CancellationToken.None));
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        var header = text.Split('\n')[0];

        Assert.StartsWith("Номер;ФИО;На работе;Норма в день;Квалификация;", header);
        Assert.Contains("Тематики", header);
        Assert.Equal(3, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // заголовок + два примера
    }

    [Fact]
    public async Task ValidFileIsLoadedInOneStep()
    {
        var file = Utf8("""
            ФИО;Норма в день;Квалификация;Тематики;Категории клиентов;Максимальная сумма;Лишняя колонка;Типы заявок;Сегменты клиентов;Минимальная сумма
            Орлова Н. П.;80;1,5;"кредит, вклад";обычный;500 000;x;"ORDER_1, ORDER_2, ORDER_3";"малый, средний";0
            Зайцев К. В.;;1;карты;"обычный; VIP";2000000;;ORDER_1;микро;0
            """);

        var preview = await Preview(file);
        Assert.Equal(2, preview.Valid);
        Assert.Equal(0, preview.Invalid);
        Assert.Equal(["Лишняя колонка"], preview.Ignored);
        Assert.Equal(0, await _f.Query(db => db.Executors.CountAsync())); // проверка ничего не пишет

        var applied = await Apply(file);
        Assert.True(applied.Applied);
        var orlova = await _f.Query(db => db.Executors.AsNoTracking().FirstAsync(e => e.FullName == "Орлова Н. П."));
        Assert.Equal(80, orlova.DailyLimit);
        Assert.Equal(1.5m, orlova.QualificationWeight);
        var attributes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(orlova.AttributesJson)!;
        Assert.Equal(["кредит", "вклад"], attributes["subjects"].EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(500000m, attributes["max_sum"].GetDecimal());
        var audit = await _f.Config(c => c.GetAuditAsync(D, null, 5, CancellationToken.None));
        Assert.Contains(audit, a => a.Action == "executors_imported");

        // загруженные сразу участвуют в распределении
        var result = await _f.Receive(1, attributes: BalancerFixture.DefaultOrder(subject: "вклад"));
        Assert.Equal(orlova.Id, result.ExecutorId);
    }

    [Fact]
    public async Task ErrorsAreReportedPerRowAndNothingIsWritten()
    {
        var file = Utf8("""
            Номер;ФИО;На работе;Тематики;Максимальная сумма;Минимальная сумма;Типы заявок;Сегменты клиентов;Категории клиентов
            10;Орлова Н. П.;да;кредит;100;0;ORDER_1;малый;обычный
            10;Зайцев К. В.;может быть;космос;много;0;ORDER_1;малый;обычный
            11;;да;вклад;100;0;ORDER_1;малый;обычный
            """);

        var result = await Apply(file);

        Assert.False(result.Applied);
        Assert.Equal(1, result.Valid);
        Assert.Equal(2, result.Invalid);
        var second = result.Rows.Single(r => r.Line == 3);
        Assert.Contains(second.Errors, e => e.Contains("уже встречался"));
        Assert.Contains(second.Errors, e => e.Contains("На работе"));
        Assert.Contains(second.Errors, e => e.Contains("космос"));
        Assert.Contains(second.Errors, e => e.StartsWith("Максимальная сумма", StringComparison.Ordinal));
        Assert.Contains(result.Rows.Single(r => r.Line == 4).Errors, e => e.StartsWith("ФИО", StringComparison.Ordinal));
        Assert.Equal(0, await _f.Query(db => db.Executors.CountAsync()));
    }

    [Fact]
    public async Task ExcelCp1251WithCommasIsUnderstood()
    {
        var text = "ФИО,Тематики,На работе,Типы заявок,Сегменты клиентов,Категории клиентов,Минимальная сумма,Максимальная сумма\r\n"
            + "Орлова Н. П.,\"кредит, вклад\",нет,ORDER_1,малый,обычный,0,500000\r\n";
        var file = Encoding.GetEncoding(1251).GetBytes(text);

        var result = await Apply(file);

        Assert.True(result.Applied);
        var saved = await _f.Query(db => db.Executors.AsNoTracking().SingleAsync());
        Assert.Equal("Орлова Н. П.", saved.FullName);
        Assert.False(saved.IsActive);
    }

    [Fact]
    public async Task ExistingExecutorIsUpdatedOrMovedFromOtherDepartment()
    {
        await _f.AddExecutor(1);
        var support = await _f.Departments(async d => (await d.FindByCodeAsync("support", CancellationToken.None))!.Value);
        await _f.Run(async b =>
        {
            await b.UpsertExecutorAsync(support, new IncomingExecutor(2, "Оператор", true, null, 1m,
                BalancerFixture.Attributes(new { })), CancellationToken.None);
            return true;
        });

        var preview = await Preview(Utf8("Номер;ФИО\n1;Исполнитель 1 (обновлён)\n2;Оператор\n"));

        Assert.Equal("update", preview.Rows[0].Action);
        Assert.Equal("move", preview.Rows[1].Action);
        Assert.Contains("Колл-центр", preview.Rows[1].Note);
    }

    [Fact]
    public void CsvHandlesQuotesAndNewlines()
    {
        var rows = Csv.Parse("a;b\n\"x;y\";\"line1\nline2\"\n\"he said \"\"hi\"\"\";z");
        Assert.Equal(["x;y", "line1\nline2"], rows[1]);
        Assert.Equal("he said \"hi\"", rows[2][0]);
    }
}
