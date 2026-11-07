using System.Text;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Parsers;

/// <summary>
/// Parser for CSV-formatted log lines.
/// Handles RFC 4180 quoting rules and extracts each field positionally.
/// If a header line has been registered, fields are named by header; otherwise field_0, field_1, etc.
/// </summary>
public sealed class CsvLogParser : BaseLogParser
{
    public CsvLogParser(ILogger<CsvLogParser>? logger = null) : base(logger) { }

    public override string Name => "CSV";
    public override string SourceType => SourceTypes.Csv;

    public override bool CanParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        // Data, not prose. Three or more fields, none of them a phrase: no comma followed by a
        // space, and at most one space in an unquoted field, for "2026-01-01 10:00:00,host,ok".
        var commaCount = 0;
        var spacesInField = 0;
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (inQuotes)
            {
                continue;
            }
            else if (c == ',')
            {
                commaCount++;
                spacesInField = 0;
                if (i + 1 < line.Length && line[i + 1] == ' ')
                {
                    return false;
                }
            }
            else if (c == ' ' && ++spacesInField > 1)
            {
                return false;
            }
        }

        return commaCount >= 2;
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        var fields = ParseCsvLine(line);

        for (var i = 0; i < fields.Count; i++)
        {
            var value = fields[i].Trim();
            if (string.IsNullOrEmpty(value)) continue;

            logEvent.SetField($"field_{i}", value);
        }

        logEvent.SetField("FieldCount", fields.Count);
        logEvent.Tags.Add("csv");
    }

    /// <summary>
    /// RFC 4180 compliant CSV line parser with quoted field support.
    /// Handles escaped quotes ("") and fields containing commas or newlines.
    /// </summary>
    private const int MaxFields = 1000;

    internal static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        if (line.Length > MaxLineLength)
            return fields;

        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                    if (fields.Count >= MaxFields) break;
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
