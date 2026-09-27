using System.Globalization;
using System.Text;
using System.Text.Json;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Application.Executors;

/// <summary>Строка файла после проверки: что будет сделано и какие ошибки нашлись.</summary>
/// <param name="Line">Номер строки в файле (заголовок — 1).</param>
/// <param name="Action">new — новый сотрудник, update — обновится, move — переведётся из другого отдела.</param>
public sealed record ImportRow(int Line, long? Id, string FullName, string Action, string? Note, IReadOnlyList<string> Errors);

/// <param name="Ignored">Колонки, которых нет среди параметров отдела, — не загружаются.</param>
public sealed record ImportPreview(IReadOnlyList<ImportRow> Rows, int Valid, int Invalid, IReadOnlyList<string> Ignored,
    IReadOnlyList<string> FileErrors, bool Applied = false);

/// <summary>
/// Загрузка сотрудников из CSV (Excel: «Сохранить как → CSV»). Колонки — ФИО, номер, норма, квалификация,
/// «на работе» и параметры сотрудника отдела по названию или ключу. Сначала проверка всех строк;
/// записывается, только если ошибок нет, — одной транзакцией с записью журнала.
/// Сотрудники обычно приходят из АИС; файл — для начальной загрузки и массовых изменений.
/// </summary>
public sealed class ExecutorImportService(IBalancerDbContext db, ExecutorDirectory directory, OrderBalancer balancer,
    TimeProvider clock)
{
    public const int MaxBytes = 256 * 1024; // столько же, сколько сервер принимает в одном запросе
    public const int MaxRows = 2000;
    public const int MaxColumns = 120;

    private static readonly string[] IdNames = ["номер", "номер в аис", "id"];
    private static readonly string[] NameNames = ["фио", "сотрудник", "fullname"];
    private static readonly string[] ActiveNames = ["на работе", "активен", "isactive"];
    private static readonly string[] LimitNames = ["норма в день", "суточная норма", "лимит", "dailylimit"];
    private static readonly string[] QualificationNames = ["квалификация", "qualificationweight"];

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    static ExecutorImportService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public async Task<ImportPreview> PreviewAsync(int departmentId, byte[] file, CancellationToken cancellationToken) =>
        (await PlanAsync(departmentId, file, cancellationToken)).Preview;

    /// <summary>Проверяет файл ещё раз и загружает, если ошибок нет; иначе возвращает ту же проверку.</summary>
    public async Task<ImportPreview> ApplyAsync(int departmentId, byte[] file, CancellationToken cancellationToken)
    {
        var (preview, incoming) = await PlanAsync(departmentId, file, cancellationToken);
        if (preview.Invalid > 0 || preview.FileErrors.Count > 0 || incoming.Count == 0)
        {
            return preview;
        }

        var audit = new AuditEntry
        {
            DepartmentId = departmentId,
            Actor = "admin",
            Action = "executors_imported",
            Entity = "executor",
            EntityId = incoming.Count.ToString(CultureInfo.InvariantCulture),
            DataJson = JsonSerializer.Serialize(new
            {
                after = new
                {
                    created = preview.Rows.Count(r => r.Action == "new"),
                    updated = preview.Rows.Count(r => r.Action == "update"),
                    moved = preview.Rows.Count(r => r.Action == "move"),
                    names = incoming.Take(20).Select(e => e.FullName),
                },
            }, AuditJson),
            CreatedAt = clock.GetUtcNow(),
        };
        await balancer.ImportExecutorsAsync(departmentId, incoming, audit, cancellationToken);
        return preview with { Applied = true };
    }

    /// <summary>
    /// Шаблон для Excel: заголовки по параметрам отдела и текущие сотрудники (или две строки-примера).
    /// UTF-8 с BOM и «;» — открывается двойным щелчком в русском Excel.
    /// </summary>
    public async Task<byte[]> TemplateAsync(int departmentId, CancellationToken cancellationToken)
    {
        var snapshot = await directory.GetAsync(departmentId, cancellationToken);
        var fields = ExecutorFields(snapshot);
        var csv = new StringBuilder();
        csv.AppendLine(Row(["Номер", "ФИО", "На работе", "Норма в день", "Квалификация", .. fields.Select(f => f.Label)]));
        var executors = snapshot.Executors.Values.OrderBy(e => e.Id).ToList();
        if (executors.Count == 0)
        {
            csv.AppendLine(Row(["", "Иванов И. И.", "да", "80", "1", .. fields.Select(f => Example(f, 0))]));
            csv.AppendLine(Row(["", "Петрова А. С.", "да", "", "1,5", .. fields.Select(f => Example(f, 1))]));
        }

        foreach (var e in executors)
        {
            csv.AppendLine(Row([
                e.Id.ToString(CultureInfo.InvariantCulture), e.FullName, e.IsActive ? "да" : "нет",
                e.DailyLimit?.ToString(CultureInfo.InvariantCulture) ?? "", e.QualificationWeight.ToString("0.###", Ru),
                .. fields.Select(f => e.Values.TryGetValue(f.Key, out var v) ? Cell(v) : ""),
            ]));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    private async Task<(ImportPreview Preview, List<IncomingExecutor> Incoming)> PlanAsync(int departmentId, byte[] file,
        CancellationToken cancellationToken)
    {
        var fileErrors = new List<string>();
        if (file.Length == 0 || file.Length > MaxBytes)
        {
            fileErrors.Add($"файл пустой или больше {MaxBytes / 1024} КБ");
            return (new ImportPreview([], 0, 0, [], fileErrors), []);
        }

        var table = Csv.Parse(Decode(file));
        if (table.Count < 2)
        {
            fileErrors.Add("нет строк с сотрудниками: первая строка — заголовки, со второй — сотрудники");
            return (new ImportPreview([], 0, 0, [], fileErrors), []);
        }

        if (table.Count - 1 > MaxRows || table[0].Count > MaxColumns)
        {
            fileErrors.Add($"не больше {MaxRows} сотрудников и {MaxColumns} колонок за раз");
            return (new ImportPreview([], 0, 0, [], fileErrors), []);
        }

        var snapshot = await directory.GetAsync(departmentId, cancellationToken);
        var fields = ExecutorFields(snapshot);
        var header = table[0].Select(h => h.Trim()).ToList();
        int Column(string[] names) => header.FindIndex(h => names.Contains(h.ToLower(Ru)));
        var idColumn = Column(IdNames);
        var nameColumn = Column(NameNames);
        var activeColumn = Column(ActiveNames);
        var limitColumn = Column(LimitNames);
        var qualificationColumn = Column(QualificationNames);
        var standard = new[] { idColumn, nameColumn, activeColumn, limitColumn, qualificationColumn }.Where(i => i >= 0).ToHashSet();
        var fieldColumns = new Dictionary<int, FieldDefinition>();
        var ignored = new List<string>();
        for (var i = 0; i < header.Count; i++)
        {
            if (standard.Contains(i) || header[i].Length == 0)
            {
                continue;
            }

            var field = fields.FirstOrDefault(f => string.Equals(f.Label, header[i], StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(f.Key, header[i], StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                ignored.Add(header[i]);
            }
            else if (!fieldColumns.ContainsValue(field))
            {
                fieldColumns[i] = field;
            }
        }

        if (nameColumn < 0)
        {
            fileErrors.Add("нет колонки «ФИО» — скачайте шаблон отдела, в нём все нужные колонки");
            return (new ImportPreview([], 0, 0, ignored, fileErrors), []);
        }

        var ids = table.Skip(1).Select(r => idColumn >= 0 && idColumn < r.Count && long.TryParse(r[idColumn].Trim(),
            NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).ToList();
        var known = await db.Executors.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.DepartmentId }).ToDictionaryAsync(e => e.Id, e => e.DepartmentId, cancellationToken);
        var departments = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);
        var nextId = (await db.Executors.AsNoTracking().MaxAsync(e => (long?)e.Id, cancellationToken) ?? 0) + 1;

        var rows = new List<ImportRow>();
        var incoming = new List<IncomingExecutor>();
        var seen = new HashSet<long>();
        for (var r = 1; r < table.Count; r++)
        {
            var cells = table[r];
            string Get(int column) => column >= 0 && column < cells.Count ? cells[column].Trim() : "";
            if (cells.All(c => string.IsNullOrWhiteSpace(c)))
            {
                continue; // пустая строка в конце файла Excel
            }

            var errors = new List<string>();
            var name = Get(nameColumn);
            if (name.Length is 0 or > 300 || name.Any(char.IsControl))
            {
                errors.Add("ФИО: обязательно, до 300 символов");
            }

            long? id = null;
            var idText = Get(idColumn);
            if (idText.Length > 0)
            {
                if (long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                {
                    id = parsed;
                    if (!seen.Add(parsed))
                    {
                        errors.Add($"номер {parsed} уже встречался в файле выше");
                    }
                }
                else
                {
                    errors.Add("номер: целое положительное число или пусто — выдастся сам");
                }
            }

            var active = true;
            var activeText = Get(activeColumn);
            if (activeText.Length > 0 && !TryFlag(activeText, out active))
            {
                errors.Add("«На работе»: да или нет");
            }

            int? limit = null;
            var limitText = Get(limitColumn);
            if (limitText.Length > 0)
            {
                if (int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) && l is >= 0 and <= 100_000)
                {
                    limit = l;
                }
                else
                {
                    errors.Add("норма в день: от 0 до 100000 или пусто — без лимита");
                }
            }

            decimal? qualification = null;
            var qualificationText = Get(qualificationColumn);
            if (qualificationText.Length > 0)
            {
                if (TryNumber(qualificationText, out var q) && q is >= 0.1m and <= 100m)
                {
                    qualification = q;
                }
                else
                {
                    errors.Add("квалификация: число от 0,1 до 100");
                }
            }

            var attributes = new Dictionary<string, JsonElement>();
            foreach (var (column, field) in fieldColumns)
            {
                var text = Get(column);
                if (text.Length == 0)
                {
                    continue;
                }

                if (ToJson(field, text) is { } json)
                {
                    attributes[field.Key] = json;
                }
                else
                {
                    errors.Add($"{field.Label}: {Expected(field)}");
                }
            }

            var parseErrors = new Dictionary<string, string[]>();
            snapshot.Catalog.Parse(FieldOwner.Executor, attributes, parseErrors);
            foreach (var (key, messages) in parseErrors)
            {
                var label = fields.FirstOrDefault(f => $"attributes.{f.Key}" == key)?.Label ?? key;
                errors.AddRange(messages.Select(m => $"{label}: {m}"));
            }

            var finalId = id ?? nextId++;
            var (action, note) = id is { } existingId && known.TryGetValue(existingId, out var dept)
                ? dept == departmentId
                    ? ("update", (string?)null)
                    : ("move", $"сейчас в отделе «{departments.GetValueOrDefault(dept, "?")}» — будет переведён сюда")
                : ("new", id is null ? $"номер {finalId} выдан автоматически" : null);
            rows.Add(new ImportRow(r + 1, finalId, name, action, note, errors));
            if (errors.Count == 0)
            {
                incoming.Add(new IncomingExecutor(finalId, name, active, limit, qualification, attributes));
            }
        }

        var invalid = rows.Count(r => r.Errors.Count > 0);
        return (new ImportPreview(rows, rows.Count - invalid, invalid, ignored, fileErrors), incoming);
    }

    private static List<FieldDefinition> ExecutorFields(BalancerSnapshot snapshot) =>
        snapshot.Catalog.All.Where(f => f.Owner == FieldOwner.Executor).OrderBy(f => f.Id).ToList();

    /// <summary>UTF-8 (с BOM или без); если байты не UTF-8 — Windows-1251, как сохраняет русский Excel.</summary>
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    /// <summary>Значение ячейки — в JSON нужного типа; списки — через запятую или точку с запятой.</summary>
    private static JsonElement? ToJson(FieldDefinition field, string text) => field.Type switch
    {
        FieldType.Number => TryNumber(text, out var n) ? JsonSerializer.SerializeToElement(n) : null,
        FieldType.Boolean => TryFlag(text, out var b) ? JsonSerializer.SerializeToElement(b) : null,
        FieldType.Array => JsonSerializer.SerializeToElement(text.Split([',', ';'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
        _ => JsonSerializer.SerializeToElement(text),
    };

    private static string Expected(FieldDefinition field) => field.Type switch
    {
        FieldType.Number => "ожидалось число",
        FieldType.Boolean => "да или нет",
        _ => "неверное значение",
    };

    private static bool TryNumber(string text, out decimal value)
    {
        var normalized = text.Replace(" ", "", StringComparison.Ordinal).Replace(' '.ToString(), "", StringComparison.Ordinal)
            .Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryFlag(string text, out bool value)
    {
        switch (text.Trim().ToLower(Ru))
        {
            case "да" or "true" or "1" or "+" or "yes":
                value = true;
                return true;
            case "нет" or "false" or "0" or "-" or "no":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static string Example(FieldDefinition field, int variant) => field.Type switch
    {
        FieldType.Array => string.Join(", ", field.Options.Skip(variant).Take(2)),
        FieldType.Enum => field.Options.Length > variant ? field.Options[variant] : "",
        FieldType.Number => variant == 0 ? "100" : "500",
        FieldType.Boolean => variant == 0 ? "да" : "нет",
        _ => "",
    };

    private static string Cell(FieldValue value) => value.IsArray
        ? string.Join(", ", value.Items)
        : value.Type switch
        {
            FieldType.Boolean => value.Flag ? "да" : "нет",
            FieldType.Number => value.Number.ToString("0.###", Ru),
            _ => value.Text,
        };

    /// <summary>Строка CSV через «;»; ячейки, начинающиеся с = + - @, экранируются — чтобы Excel не счёл их формулой.</summary>
    private static string Row(IEnumerable<string> cells) => string.Join(';', cells.Select(cell =>
    {
        var safe = cell.Length > 0 && "=+-@\t\r".Contains(cell[0]) && !decimal.TryParse(cell, NumberStyles.Number, Ru, out _)
            ? "'" + cell
            : cell;
        return safe.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + safe.Replace("\"", "\"\"") + "\"" : safe;
    }));
}

/// <summary>Разбор CSV по RFC 4180: кавычки, переносы строк в ячейках; разделитель — «;» или «,» по заголовку.</summary>
public static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = firstLine.Count(c => c == ';') >= firstLine.Count(c => c == ',') ? ';' : ',';
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
