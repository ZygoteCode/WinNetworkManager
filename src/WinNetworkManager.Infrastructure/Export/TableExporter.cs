using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WinNetworkManager.Infrastructure.Export;

/// <summary>A typed export column.</summary>
/// <param name="Name">Machine-friendly column name (snake_case, includes the unit, e.g. <c>tcp_rx_bytes_per_s</c>).</param>
/// <param name="Description">Human description (written to the metadata).</param>
public sealed record ExportColumn(string Name, string Description);

/// <summary>A table to export: raw values (numbers, booleans, strings, DateTimeOffset), never pre-formatted text.</summary>
/// <param name="Title">Table title.</param>
/// <param name="Columns">Columns.</param>
/// <param name="Rows">Rows; each value is null, string, bool, a number, or DateTimeOffset (written as UTC ISO-8601).</param>
/// <param name="Metadata">Provenance: source APIs, units, window, settings, application version, generation time.</param>
public sealed record ExportTable(string Title, IReadOnlyList<ExportColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyDictionary<string, string> Metadata);

/// <summary>Writes <see cref="ExportTable"/>s as CSV (with a commented metadata preamble) or JSON.</summary>
public static class TableExporter
{
    private static readonly JsonWriterOptions JsonOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Writes CSV. Metadata lines start with '#'; numbers use the invariant culture; times are UTC ISO-8601.</summary>
    public static void WriteCsv(string path, ExportTable table, char delimiter = ',')
    {
        ArgumentNullException.ThrowIfNull(table);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        WriteCsv(writer, table, delimiter);
    }

    /// <summary>Writes CSV to a writer.</summary>
    public static void WriteCsv(TextWriter writer, ExportTable table, char delimiter = ',')
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(table);
        writer.WriteLine($"# {table.Title}");
        foreach (var (key, value) in table.Metadata)
        {
            writer.WriteLine($"# {key}: {value.Replace('\n', ' ')}");
        }

        foreach (var column in table.Columns)
        {
            writer.WriteLine($"# column {column.Name}: {column.Description}");
        }

        writer.WriteLine(string.Join(delimiter, table.Columns.Select(c => Escape(c.Name, delimiter))));
        foreach (var row in table.Rows)
        {
            writer.WriteLine(string.Join(delimiter, row.Select(v => Escape(FormatValue(v), delimiter))));
        }
    }

    /// <summary>Writes JSON: { "title", "metadata", "columns", "rows": [ { name: value } ] }.</summary>
    public static void WriteJson(string path, ExportTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        using var stream = File.Create(path);
        WriteJson(stream, table);
    }

    /// <summary>Writes JSON to a stream.</summary>
    public static void WriteJson(Stream stream, ExportTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        using var json = new Utf8JsonWriter(stream, JsonOptions);
        json.WriteStartObject();
        json.WriteString("title", table.Title);
        json.WriteStartObject("metadata");
        foreach (var (key, value) in table.Metadata)
        {
            json.WriteString(key, value);
        }

        json.WriteEndObject();
        json.WriteStartArray("columns");
        foreach (var column in table.Columns)
        {
            json.WriteStartObject();
            json.WriteString("name", column.Name);
            json.WriteString("description", column.Description);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("rows");
        foreach (var row in table.Rows)
        {
            json.WriteStartObject();
            for (int i = 0; i < table.Columns.Count; i++)
            {
                object? value = i < row.Count ? row[i] : null;
                string name = table.Columns[i].Name;
                switch (value)
                {
                    case null:
                        json.WriteNull(name);
                        break;
                    case bool b:
                        json.WriteBoolean(name, b);
                        break;
                    case int n:
                        json.WriteNumber(name, n);
                        break;
                    case long n:
                        json.WriteNumber(name, n);
                        break;
                    case ulong n:
                        json.WriteNumber(name, n);
                        break;
                    case double d when double.IsFinite(d):
                        json.WriteNumber(name, Math.Round(d, 3));
                        break;
                    case double:
                        json.WriteNull(name);
                        break;
                    case DateTimeOffset t:
                        json.WriteString(name, t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                        break;
                    default:
                        json.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        double d when !double.IsFinite(d) => string.Empty,
        double d => Math.Round(d, 3).ToString("R", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Escape(string value, char delimiter)
    {
        bool quote = value.Contains(delimiter, StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) ||
                     value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ||
                     (value.Length > 0 && (value[0] is '=' or '+' or '-' or '@') && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        if (!quote)
        {
            return value;
        }

        // Leading formula characters are prefixed with an apostrophe so spreadsheets do not execute them.
        string safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? "'" + value
            : value;
        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
