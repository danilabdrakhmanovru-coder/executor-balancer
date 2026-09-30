using System.IO.Compression;
using System.Xml.Linq;
using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Infrastructure.Export;

namespace ExecutorBalancer.Tests;

public class ExcelExportTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static AnalyticsReport Report(params ExecutorMetrics[] executors) => new(
        AnalyticsPeriod.Today,
        new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 1, 1, 20, 0, TimeSpan.Zero),
        1,
        [new TimelinePoint(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), 3, 4.5m, 2, 1)],
        executors,
        new KindBreakdown(3, 0, 0, 0),
        new FairnessSummary(1.6m, 4.2m, 2, 4.5m));

    private static ExecutorMetrics Person(long id, string name) =>
        new(id, name, true, 1.5m, 3, 4.5m, 3, 0, 0, 0, 4.5m, 4.5m, 0.4m, 2, 1, 50m, Rank: null);

    private static Dictionary<string, XDocument> Open(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.Where(e => e.Length > 0).ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            return XDocument.Load(stream);
        });
    }

    [Fact]
    public void BookHasTwoSheetsWithFrozenHeaderAndFilter()
    {
        var parts = Open(AnalyticsWorkbook.Build(Report(Person(85, "Андреева А."), Person(86, "Белов Б.")), "Банк",
            TimeZoneInfo.Utc));

        Assert.Contains("[Content_Types].xml", parts.Keys);
        Assert.Contains("xl/styles.xml", parts.Keys);
        var sheets = parts["xl/workbook.xml"].Descendants(Main + "sheet").Select(s => (string)s.Attribute("name")!).ToList();
        Assert.Equal(["Сотрудники", "По времени"], sheets);

        var staff = parts["xl/worksheets/sheet1.xml"];
        var pane = staff.Descendants(Main + "pane").Single();
        Assert.Equal("8", (string)pane.Attribute("ySplit")!);
        Assert.Equal("A8:U10", (string)staff.Descendants(Main + "autoFilter").Single().Attribute("ref")!);
        Assert.Equal(21, staff.Descendants(Main + "col").Count());

        // числа — числами (без t="inlineStr"), а «вне рейтинга» — текстом
        var row = staff.Descendants(Main + "row").Single(r => (string)r.Attribute("r")! == "9");
        var id = row.Elements(Main + "c").First();
        Assert.Null(id.Attribute("t"));
        Assert.Equal("85", id.Element(Main + "v")!.Value);
        Assert.Contains(row.Descendants(Main + "t"), t => t.Value == "вне рейтинга");

        // дата на втором листе — число с форматом даты
        var date = parts["xl/worksheets/sheet2.xml"].Descendants(Main + "row")
            .Single(r => (string)r.Attribute("r")! == "5").Elements(Main + "c").First();
        Assert.Equal(new DateTime(2026, 10, 1).ToOADate(), double.Parse(date.Element(Main + "v")!.Value,
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NamesFromOutsideStayPlainText()
    {
        var parts = Open(AnalyticsWorkbook.Build(Report(Person(1, "=HYPERLINK(\"http://evil\")\u0001<b>")), "Отдел & Ко",
            TimeZoneInfo.Utc));

        var staff = parts["xl/worksheets/sheet1.xml"];
        Assert.Empty(staff.Descendants(Main + "f"));
        var name = staff.Descendants(Main + "row").Single(r => (string)r.Attribute("r")! == "9")
            .Elements(Main + "c").ElementAt(1);
        Assert.Equal("inlineStr", (string)name.Attribute("t")!);
        Assert.Equal("=HYPERLINK(\"http://evil\")<b>", name.Value);
        Assert.Contains(staff.Descendants(Main + "t"), t => t.Value == "Отчёт по отделу «Отдел & Ко»");
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(20, "U")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    public void ColumnLetters(int index, string expected) => Assert.Equal(expected, XlsxWorkbook.Column(index));
}
