using ExecutorBalancer.Application.Insights;

namespace ExecutorBalancer.Tests;

/// <summary>Спрос и покрытие по заявкам и ИИ-разбор: факты без ИИ, обезличенная сводка, разбор ответа модели.</summary>
public class InsightsTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    /// <summary>Три сотрудника умеют только кредиты; из десяти заявок две — ипотека, брать их некому.</summary>
    private async Task SeedAsync()
    {
        for (var id = 1; id <= 3; id++)
        {
            await _f.AddExecutor(id, subjects: ["кредит"]);
        }

        for (var id = 1; id <= 10; id++)
        {
            await _f.Receive(id, attributes: BalancerFixture.DefaultOrder(id <= 8 ? "кредит" : "ипотека"));
        }
    }

    [Fact]
    public async Task DemandShowsSkillGap()
    {
        await SeedAsync();

        var report = await _f.Demand();

        Assert.Equal(10, report.Sampled);
        Assert.Equal(10, report.Totals.Received);
        Assert.Equal(2, report.Totals.WaitingNow);
        Assert.Equal(3, report.Totals.ActiveExecutors);
        var subject = report.Fields.Single(f => f.Key == "subject");
        var credit = subject.Options.Single(o => o.Value == "кредит");
        Assert.Equal((8, 80m, 3, 100m, 0.8m), (credit.Orders, credit.SharePercent, credit.Executors, credit.CapacitySharePercent, credit.Tension));
        var mortgage = subject.Options.Single(o => o.Value == "ипотека");
        Assert.Equal((2, 2, 0), (mortgage.Orders, mortgage.Waiting, mortgage.Executors));
        Assert.Null(mortgage.Tension); // брать некому — не «напряжение», а пробел в навыках
        Assert.Equal(2, report.Unmatched);
        Assert.Equal(2, Assert.Single(report.Blocked).Orders);
        Assert.Contains(report.Waiting, w => w.Orders == 2);
    }

    [Fact]
    public async Task WithoutModelAnalysisIsUnavailable()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<AiUnavailableException>(() => _f.Insights(ai => ai.AnalyzeAsync(D, CancellationToken.None)));
        Assert.Empty(_f.Ai.Requests);
    }

    [Fact]
    public async Task ModelGetsAnonymousSummaryAndAnswerIsReused()
    {
        await SeedAsync();
        _f.Ai.IsConfigured = true;
        _f.Ai.Answer = """
            Вот разбор:
            ```json
            {"summary": "Ипотеку брать некому.",
             "findings": [{"level": "HIGH", "title": "Нет навыка «ипотека»", "detail": "2 заявки ждут, сотрудников 0"}],
             "recommendations": ["Обучить С-1 ипотеке"]}
            ```
            """;

        var first = await _f.Insights(ai => ai.AnalyzeAsync(D, CancellationToken.None));
        var second = await _f.Insights(ai => ai.AnalyzeAsync(D, CancellationToken.None));

        Assert.True(first.Structured);
        Assert.Equal("Ипотеку брать некому.", first.Summary);
        var finding = Assert.Single(first.Findings);
        Assert.Equal("high", finding.Level);
        Assert.Equal(["Обучить С-1 ипотеке"], first.Recommendations);
        Assert.True(second.Cached); // повтор в течение минуты — без нового запроса к модели
        var context = Assert.Single(_f.Ai.Requests);
        Assert.Contains("С-1", context, StringComparison.Ordinal);
        Assert.Contains("ипотека", context, StringComparison.Ordinal);
        Assert.DoesNotContain("Исполнитель", context, StringComparison.Ordinal); // ФИО в модель не уходят
    }

    [Fact]
    public void AnswerOutsideSchemaIsShownAsText()
    {
        var now = DateTimeOffset.UnixEpoch;

        var text = AiAnalyst.Parse("Не могу ответить в JSON, но вот мысль: " + new string('а', 5000), "m", now);
        Assert.False(text.Structured);
        Assert.Empty(text.Findings);
        Assert.True(text.Summary.Length <= 3000);

        var odd = AiAnalyst.Parse("""{"summary": "ок", "findings": [{"level": "критично", "title": "т"}, {}], "recommendations": ["", "да"]}""", "m", now);
        Assert.Equal("medium", Assert.Single(odd.Findings).Level);
        Assert.Equal(["да"], odd.Recommendations);
    }
}
