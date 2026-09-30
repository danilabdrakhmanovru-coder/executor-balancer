using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ExecutorBalancer.Infrastructure.Export;

/// <summary>Оформление ячейки. Номера совпадают с порядком cellXfs в <see cref="XlsxWorkbook"/>.</summary>
public enum XlsxStyle
{
    Default = 0,
    Title = 1,
    Subtitle = 2,
    Label = 3,
    Header = 4,
    Text = 5,
    Integer = 6,
    Number = 7,
    Percent = 8,
    DateTime = 9,
    LabelPercent = 10,
}

/// <summary>Ячейка: текст, число или дата. Пустое значение со стилем таблицы — рамка без содержимого.</summary>
public readonly record struct XlsxCell(object? Value, XlsxStyle Style)
{
    public static XlsxCell Text(string? value, XlsxStyle style = XlsxStyle.Text) => new(value, style);

    public static XlsxCell Number(decimal? value, XlsxStyle style = XlsxStyle.Number) => new(value, style);

    public static XlsxCell Date(DateTime value, XlsxStyle style = XlsxStyle.DateTime) => new(value, style);
}

/// <summary>Лист: строки сверху вниз, ширины столбцов, закреплённые строки, фильтр и объединённые ячейки.</summary>
public sealed class XlsxSheet(string name)
{
    public string Name { get; } = name;

    /// <summary>Ширины столбцов в символах, по порядку с A.</summary>
    public IList<double> Widths { get; } = [];

    /// <summary>Сколько верхних строк не прокручивается (обычно — до строки заголовков включительно).</summary>
    public int FrozenRows { get; set; }

    /// <summary>Диапазон автофильтра, например «A8:U20».</summary>
    public string? AutoFilter { get; set; }

    public IList<string> Merges { get; } = [];

    internal List<(double? Height, XlsxCell[] Cells)> Rows { get; } = [];

    public XlsxSheet Row(params XlsxCell[] cells)
    {
        Rows.Add((null, cells));
        return this;
    }

    public XlsxSheet Row(double height, params XlsxCell[] cells)
    {
        Rows.Add((height, cells));
        return this;
    }

    public int RowCount => Rows.Count;
}

/// <summary>
/// Минимальная книга Excel (.xlsx) без сторонних библиотек: ZIP с разметкой SpreadsheetML.
/// Текст пишется как встроенная строка (inlineStr) — Excel никогда не исполняет её как формулу,
/// поэтому имя из внешней системы вроде «=HYPERLINK(...)» остаётся просто текстом.
/// </summary>
public static class XlsxWorkbook
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static byte[] Build(IReadOnlyList<XlsxSheet> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
            Add(zip, "_rels/.rels",
                $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{RelNs}/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/workbook.xml", Workbook(sheets));
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
            Add(zip, "xl/styles.xml", Styles);
            for (var i = 0; i < sheets.Count; i++)
            {
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i]));
            }
        }

        return stream.ToArray();
    }

    /// <summary>Буквы столбца: 0 → A, 25 → Z, 26 → AA.</summary>
    public static string Column(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + (n - 1) % 26) + name;
        }

        return name;
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        writer.Write('\n');
        writer.Write(content);
    }

    private static string ContentTypes(int sheetCount)
    {
        var sb = new StringBuilder(
            """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        }

        return sb.Append("</Types>").ToString();
    }

    private static string Workbook(IReadOnlyList<XlsxSheet> sheets)
    {
        var sb = new StringBuilder($"""<workbook xmlns="{MainNs}" xmlns:r="{RelNs}"><bookViews><workbookView/></bookViews><sheets>""");
        for (var i = 0; i < sheets.Count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"""<sheet name="{Escape(sheets[i].Name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
        }

        sb.Append("</sheets>");
        var filters = sheets.Select((s, i) => (s, i)).Where(x => x.s.AutoFilter is not null).ToList();
        if (filters.Count > 0)
        {
            // Excel ждёт скрытое имя диапазона фильтра — без него фильтр работает, но при сохранении файл «чинится»
            sb.Append("<definedNames>");
            foreach (var (sheet, index) in filters)
            {
                var range = string.Join(':', sheet.AutoFilter!.Split(':').Select(Absolute));
                sb.Append(CultureInfo.InvariantCulture,
                    $"""<definedName name="_xlnm._FilterDatabase" localSheetId="{index}" hidden="1">{Escape($"'{sheet.Name.Replace("'", "''", StringComparison.Ordinal)}'!{range}")}</definedName>""");
            }

            sb.Append("</definedNames>");
        }

        return sb.Append("</workbook>").ToString();

        static string Absolute(string cell)
        {
            var split = cell.AsSpan().IndexOfAnyInRange('0', '9');
            return $"${cell[..split]}${cell[split..]}";
        }
    }

    private static string WorkbookRels(int sheetCount)
    {
        var sb = new StringBuilder("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{i}" Type="{RelNs}/worksheet" Target="worksheets/sheet{i}.xml"/>""");
        }

        sb.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{sheetCount + 1}" Type="{RelNs}/styles" Target="styles.xml"/>""");
        return sb.Append("</Relationships>").ToString();
    }

    private static string Sheet(XlsxSheet sheet)
    {
        var sb = new StringBuilder($"""<worksheet xmlns="{MainNs}" xmlns:r="{RelNs}"><sheetPr><pageSetUpPr fitToPage="1"/></sheetPr>""");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
        if (sheet.FrozenRows > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"""<pane ySplit="{sheet.FrozenRows}" topLeftCell="A{sheet.FrozenRows + 1}" activePane="bottomLeft" state="frozen"/><selection pane="bottomLeft" activeCell="A{sheet.FrozenRows + 1}" sqref="A{sheet.FrozenRows + 1}"/>""");
        }

        sb.Append("</sheetView></sheetViews><sheetFormatPr defaultRowHeight=\"15\"/>");
        if (sheet.Widths.Count > 0)
        {
            sb.Append("<cols>");
            for (var i = 0; i < sheet.Widths.Count; i++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"""<col min="{i + 1}" max="{i + 1}" width="{sheet.Widths[i]:0.##}" customWidth="1"/>""");
            }

            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var (height, cells) = sheet.Rows[r];
            sb.Append(CultureInfo.InvariantCulture, $"""<row r="{r + 1}" """);
            if (height is { } h)
            {
                sb.Append(CultureInfo.InvariantCulture, $"""ht="{h:0.##}" customHeight="1" """);
            }

            sb.Append('>');
            for (var c = 0; c < cells.Length; c++)
            {
                AppendCell(sb, $"{Column(c)}{r + 1}", cells[c]);
            }

            sb.Append("</row>");
        }

        sb.Append("</sheetData>");
        if (sheet.AutoFilter is { } filter)
        {
            sb.Append(CultureInfo.InvariantCulture, $"""<autoFilter ref="{filter}"/>""");
        }

        if (sheet.Merges.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"""<mergeCells count="{sheet.Merges.Count}">""");
            foreach (var merge in sheet.Merges)
            {
                sb.Append(CultureInfo.InvariantCulture, $"""<mergeCell ref="{merge}"/>""");
            }

            sb.Append("</mergeCells>");
        }

        sb.Append("""<pageMargins left="0.4" right="0.4" top="0.5" bottom="0.5" header="0.3" footer="0.3"/><pageSetup orientation="landscape" paperSize="9" fitToWidth="1" fitToHeight="0"/>""");
        return sb.Append("</worksheet>").ToString();
    }

    private static void AppendCell(StringBuilder sb, string reference, XlsxCell cell)
    {
        var style = (int)cell.Style;
        switch (cell.Value)
        {
            case null:
                if (style != 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" s="{style}"/>""");
                }

                break;
            case string text:
                sb.Append(CultureInfo.InvariantCulture,
                    $"""<c r="{reference}" s="{style}" t="inlineStr"><is><t xml:space="preserve">{Escape(text)}</t></is></c>""");
                break;
            case DateTime date:
                sb.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" s="{style}"><v>{date.ToOADate():0.#########}</v></c>""");
                break;
            case decimal number:
                sb.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" s="{style}"><v>{number}</v></c>""");
                break;
            default:
                throw new ArgumentException($"неподдерживаемое значение ячейки: {cell.Value.GetType().Name}", nameof(cell));
        }
    }

    /// <summary>Экранирование для XML; управляющие символы (кроме табуляции и перевода строки) в XML недопустимы — выбрасываем.</summary>
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\t' or '\n' or '\r': sb.Append(ch); break;
                case < ' ' or '￾' or '￿': break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }

    // шрифты: 0 обычный, 1 жирный, 2 заголовок листа, 3 серый мелкий; заливки 0 и 1 обязательны, 2 — шапка таблицы;
    // рамки: 0 нет, 1 тонкая; форматы: 3 «# ##0», 164 «0,0», 165 дата и время
    private const string Styles =
        """<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""" +
        """<numFmts count="2"><numFmt numFmtId="164" formatCode="0.0"/><numFmt numFmtId="165" formatCode="dd.mm.yyyy hh:mm"/></numFmts>""" +
        """<fonts count="4"><font><sz val="11"/><name val="Calibri"/><family val="2"/></font>""" +
        """<font><b/><sz val="11"/><name val="Calibri"/><family val="2"/></font>""" +
        """<font><b/><sz val="14"/><color rgb="FF14213D"/><name val="Calibri"/><family val="2"/></font>""" +
        """<font><sz val="10"/><color rgb="FF5B6676"/><name val="Calibri"/><family val="2"/></font></fonts>""" +
        """<fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>""" +
        """<fill><patternFill patternType="solid"><fgColor rgb="FFE9E3F7"/><bgColor indexed="64"/></patternFill></fill></fills>""" +
        """<borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border>""" +
        """<border><left style="thin"><color rgb="FFD0D5DD"/></left><right style="thin"><color rgb="FFD0D5DD"/></right>""" +
        """<top style="thin"><color rgb="FFD0D5DD"/></top><bottom style="thin"><color rgb="FFD0D5DD"/></bottom><diagonal/></border></borders>""" +
        """<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>""" +
        """<cellXfs count="11">""" +
        """<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>""" +
        """<xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/>""" +
        """<xf numFmtId="0" fontId="3" fillId="0" borderId="0" xfId="0" applyFont="1"/>""" +
        """<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>""" +
        """<xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>""" +
        """<xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1"/>""" +
        """<xf numFmtId="3" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1"/>""" +
        """<xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1"/>""" +
        """<xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1"/>""" +
        """<xf numFmtId="165" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1" applyAlignment="1"><alignment horizontal="left"/></xf>""" +
        """<xf numFmtId="164" fontId="1" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1" applyAlignment="1"><alignment horizontal="left"/></xf>""" +
        """</cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""";
}
