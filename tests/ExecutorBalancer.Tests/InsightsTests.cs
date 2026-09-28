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
             "notes": [{"id": "s1", "why": "Ни у кого нет навыка «ипотека».",
                        "actions": [{"text": "Обучить С-1 ипотеке", "who": "Руководитель", "effect": "заявки перестанут ждать"},
                                    {"text": "Проверить правило «Тематика»", "who": "администратор"}]}],
             "extra": ["Добавить сотрудника на ипотеку"]}
            ```
            """;

        var first = await _f.Insights(ai => ai.AnalyzeAsync(D, CancellationToken.None));
        var second = await _f.Insights(ai => ai.AnalyzeAsync(D, CancellationToken.None));

        Assert.True(first.Structured);
        Assert.Equal("Ипотеку брать некому.", first.Summary);
        // находки — от сервера: точные заголовок и цифры, ссылка на значение параметра
        var gap = first.Findings.Single(f => f.Id == "s1");
        Assert.Equal(("skills", "high", "subject", "ипотека"), (gap.Category, gap.Level, gap.FieldKey, gap.Value));
        Assert.Equal("Некому брать: тематика «ипотека»", gap.Title);
        // от модели — пояснение и действия с пометкой «кому»
        Assert.Equal("Ни у кого нет навыка «ипотека».", gap.Why);
        Assert.Equal([("руководитель", "заявки перестанут ждать"), ("администратор", null)],
            gap.Actions.Select(a => (a.Who, a.Effect)));
        Assert.Contains(first.Findings, f => f.Category == "queue" && f.Why is null && f.Actions.Count == 0);
        Assert.Equal("Добавить сотрудника на ипотеку", Assert.Single(first.Recommendations).Text);
        Assert.True(second.Cached); // повтор в течение минуты — без нового запроса к модели
        var context = Assert.Single(_f.Ai.Requests);
        Assert.Contains("\"signals\"", context, StringComparison.Ordinal);
        Assert.Contains("«Тематика: ипотека» — 2 заявки (20%), но ни один активный сотрудник", context, StringComparison.Ordinal);
        Assert.DoesNotContain("Исполнитель", context, StringComparison.Ordinal); // ФИО в модель не уходят
    }

    [Fact]
    public void SignalsFlagRealProblemsOnly()
    {
        DemandOption Option(string value, int orders, decimal share, int executors, decimal capacity, int waiting = 0) =>
            new(value, orders, share, waiting, 0, executors, capacity, capacity > 0 ? Math.Round(share / capacity, 2) : null);
        var report = new DemandReport(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 100,
            new DemandTotals(100, 100, 90, 5, 3, 1, 5, 10),
            [new DemandField("region", "Регион", [
                Option("Самара", 22, 22, 7, 70),      // сил с запасом — не проблема
                Option("Уфа", 37, 37.4m, 4, 40),      // поровну — не проблема
                Option("Казань", 30, 30, 1, 10, 3),   // сил втрое меньше, заявки ждут
                Option("Пермь", 11, 11, 0, 0, 2)])],   // брать некому
            [], 100, 0, [new WaitingReason("нет подходящего", 3)]);
        var day = new ExecutorBalancer.Application.Analytics.AnalyticsReport(
            ExecutorBalancer.Application.Analytics.AnalyticsPeriod.Day, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, [],
            [Metrics(2010, closed: 9, returned: 10, quality: 0.32m), Metrics(2011, closed: 40, returned: 2, quality: 0.95m)],
            new(0, 0, 0, 0), new(1.4m, 3m, 2, 0));

        var signals = AiAnalyst.Signals(report, day, qualityThreshold: 0.8m);

        Assert.DoesNotContain(signals, x => x.Text.Contains("Самара", StringComparison.Ordinal) || x.Text.Contains("Уфа", StringComparison.Ordinal));
        Assert.Contains(signals, x => x.Level == "high" && x.Text.Contains("«Регион: Пермь»", StringComparison.Ordinal));
        Assert.Contains(signals, x => x.Level == "high" && x.Text.Contains("«Регион: Казань»", StringComparison.Ordinal));
        Assert.Contains(signals, x => x.Executors.Contains(2010));
        Assert.DoesNotContain(signals, x => x.Executors.Contains(2011));
        Assert.Equal("high", signals[0].Level); // важное — первым
    }

    private static ExecutorBalancer.Application.Analytics.ExecutorMetrics Metrics(long id, int closed, int returned, decimal quality) =>
        new(id, $"Сотрудник {id}", true, 1m, closed + returned, closed + returned, closed, 0, 0, returned, closed, closed, null,
            closed, returned, Math.Round(returned * 100m / (closed + returned), 1), Quality: quality);

    [Fact]
    public void AnswerOutsideSchemaIsShownAsText()
    {
        var now = DateTimeOffset.UnixEpoch;

        AiAnalyst.Signal[] signals = [new("queue", "high", "Заявки ждут исполнителя", "Ждут 3 заявки.", []) { Id = "s1" }];

        // не по схеме — находки сервера всё равно на месте, текст модели — как есть
        var text = AiAnalyst.Parse("Не могу ответить в JSON, но вот мысль: " + new string('а', 5000), "m", now, signals);
        Assert.False(text.Structured);
        Assert.Equal("Заявки ждут исполнителя", Assert.Single(text.Findings).Title);
        Assert.True(text.Summary.Length <= 3000);

        // лишние и пустые записи модели не ломают отчёт; чужой id не создаёт находку
        var odd = AiAnalyst.Parse("""
            {"summary": "ок", "notes": [{"id": "s9", "why": "выдумка"}, {}, "строка"],
             "extra": ["", {"text": "да", "who": "директор"}, 5]}
            """, "m", now, signals);
        Assert.Null(Assert.Single(odd.Findings).Why);
        Assert.Equal(("да", "руководитель"), (Assert.Single(odd.Recommendations).Text, odd.Recommendations[0].Who));
    }

    [Theory]
    [InlineData("gpt://b1gabcdef/yandexgpt/latest", "YandexGPT Pro")]
    [InlineData("gpt://b1gabcdef/yandexgpt-lite/latest", "YandexGPT Lite")]
    [InlineData("qwen2.5:7b", "qwen2.5:7b")]
    public void ModelTitleHidesFolderId(string model, string title) => Assert.Equal(title, AiAnalyst.Title(model));
}
