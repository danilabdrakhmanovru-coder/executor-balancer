using System.Globalization;
using ExecutorBalancer.Application.Analytics;

namespace ExecutorBalancer.Infrastructure.Export;

/// <summary>
/// Отчёт «Аналитики» для Excel: лист «Сотрудники» (сводка и таблица с фильтром, шапка закреплена)
/// и лист «По времени» (динамика по интервалам). Числа — числами, даты — датами, поэтому в Excel их можно
/// сортировать, суммировать и строить по ним графики.
/// </summary>
public static class AnalyticsWorkbook
{
    private static readonly (string Title, double Width)[] ExecutorColumns =
    [
        ("ID", 8), ("Сотрудник", 24), ("На работе", 10), ("Опыт", 9), ("Назначено", 11),
        ("Вес назначенных", 12), ("Первичные", 11), ("Перераспределения", 19), ("От родителя", 11), ("Вторичные", 11),
        ("Вес свободного выбора", 13), ("Справедливый вес", 14), ("Отклонение, %", 12), ("Решено и отклонено", 12),
        ("На доработку", 12), ("Доля возвратов, %", 12), ("Место в рейтинге", 12), ("Баллы рейтинга", 11),
        ("Качество, %", 11), ("Закрыто подозрительно быстро", 14), ("Сверх нормы", 10),
    ];

    private static readonly (string Title, double Width)[] TimelineColumns =
    [
        ("Начало интервала", 18), ("Назначено", 12), ("Вес", 10), ("Решено и отклонено", 14), ("На доработку", 13),
    ];

    public static byte[] Build(AnalyticsReport report, string departmentName, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        DateTime Local(DateTimeOffset moment) => TimeZoneInfo.ConvertTime(moment, zone).DateTime;
        var period = $"Период: {Local(report.From).ToString("dd.MM.yyyy HH:mm", culture)} — "
                     + $"{Local(report.To).ToString("dd.MM.yyyy HH:mm", culture)} ({zone.Id})";

        var staff = new XlsxSheet("Сотрудники");
        foreach (var (_, width) in ExecutorColumns)
        {
            staff.Widths.Add(width);
        }

        staff.Row(24, XlsxCell.Text($"Отчёт по отделу «{departmentName}»", XlsxStyle.Title))
            .Row(XlsxCell.Text(period, XlsxStyle.Subtitle))
            .Row()
            .Row(XlsxCell.Text("Среднее отклонение от справедливой доли, %", XlsxStyle.Label), Blank, Blank, Blank,
                XlsxCell.Number(report.Fairness.MeanAbsDeviationPercent, XlsxStyle.LabelPercent))
            .Row(XlsxCell.Text("Максимальное отклонение, %", XlsxStyle.Label), Blank, Blank, Blank,
                XlsxCell.Number(report.Fairness.MaxAbsDeviationPercent, XlsxStyle.LabelPercent))
            .Row(XlsxCell.Text("Норма — до 2%. Пусто — данных за период пока мало.", XlsxStyle.Subtitle))
            .Row();
        staff.Merges.Add("A4:D4");
        staff.Merges.Add("A5:D5");

        var headerRow = staff.RowCount + 1;
        staff.Row(48, ExecutorColumns.Select(c => XlsxCell.Text(c.Title, XlsxStyle.Header)).ToArray());
        foreach (var e in report.Executors)
        {
            staff.Row(
                XlsxCell.Number(e.Id),
                XlsxCell.Text(e.Name),
                XlsxCell.Text(e.IsActive ? "да" : "нет"),
                XlsxCell.Number(e.Qualification),
                XlsxCell.Number(e.Assigned, XlsxStyle.Integer),
                XlsxCell.Number(e.AssignedWeight),
                XlsxCell.Number(e.Primary, XlsxStyle.Integer),
                XlsxCell.Number(e.Reassign, XlsxStyle.Integer),
                XlsxCell.Number(e.Parent, XlsxStyle.Integer),
                XlsxCell.Number(e.Secondary, XlsxStyle.Integer),
                XlsxCell.Number(e.FreeWeight),
                XlsxCell.Number(e.FairWeight),
                XlsxCell.Number(e.DeviationPercent, XlsxStyle.Percent),
                XlsxCell.Number(e.Closed, XlsxStyle.Integer),
                XlsxCell.Number(e.Returned, XlsxStyle.Integer),
                XlsxCell.Number(e.ReturnRatePercent, XlsxStyle.Percent),
                e.Rank is { } rank ? XlsxCell.Number(rank, XlsxStyle.Integer) : XlsxCell.Text("вне рейтинга"),
                XlsxCell.Number(e.Points),
                XlsxCell.Number(e.Quality is { } q ? Math.Round(q * 100m, 1) : null, XlsxStyle.Percent),
                XlsxCell.Number(e.FastClosed, XlsxStyle.Integer),
                XlsxCell.Number(e.Extra, XlsxStyle.Integer));
        }

        staff.FrozenRows = headerRow;
        staff.AutoFilter = $"A{headerRow}:{XlsxWorkbook.Column(ExecutorColumns.Length - 1)}{Math.Max(staff.RowCount, headerRow + 1)}";

        var timeline = new XlsxSheet("По времени");
        foreach (var (_, width) in TimelineColumns)
        {
            timeline.Widths.Add(width);
        }

        var step = report.BucketMinutes > 0 ? $"{report.BucketMinutes} мин" : $"{report.BucketHours} ч";
        timeline.Row(24, XlsxCell.Text($"Динамика по отделу «{departmentName}»", XlsxStyle.Title))
            .Row(XlsxCell.Text($"{period}; шаг — {step}", XlsxStyle.Subtitle))
            .Row()
            .Row(32, TimelineColumns.Select(c => XlsxCell.Text(c.Title, XlsxStyle.Header)).ToArray());
        foreach (var point in report.Timeline)
        {
            timeline.Row(
                XlsxCell.Date(Local(point.Start)),
                XlsxCell.Number(point.Assigned, XlsxStyle.Integer),
                XlsxCell.Number(point.AssignedWeight),
                XlsxCell.Number(point.Closed, XlsxStyle.Integer),
                XlsxCell.Number(point.Returned, XlsxStyle.Integer));
        }

        timeline.FrozenRows = 4;
        return XlsxWorkbook.Build([staff, timeline]);
    }

    private static XlsxCell Blank => new(null, XlsxStyle.Default);
}
