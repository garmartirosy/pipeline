using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace DBMonitor.Services.Import;

/// <summary>
/// Streams converted rows from a CSV file as object?[] arrays.
/// Each array is ordered to match the non-skipped ColumnMappings in ImportRequest.
/// Rows that fail type conversion are yielded as errors via the onError callback.
/// </summary>
internal static class CsvRowProducer
{
    private const long MaxRows = 5_000_000;

    public static async IAsyncEnumerable<ProducerRow> ProduceAsync(
        ImportRequest request,
        CultureInfo culture,
        Action<RowError> onError,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var encoding = Encoding.GetEncoding(request.EncodingName);
        var activeMappings = request.Mappings.Where(m => !m.Skip).ToArray();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter        = request.Delimiter.ToString(),
            HasHeaderRecord  = request.HasHeader,
            MissingFieldFound = null,
            BadDataFound     = null,
            IgnoreBlankLines = false,
            DetectDelimiter  = false,
        };

        await using var fs = File.OpenRead(request.TempFilePath);
        using var textReader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(textReader, config);

        if (request.HasHeader)
        {
            await csv.ReadAsync();
            csv.ReadHeader();
        }

        long lineNumber = request.HasHeader ? 1 : 0;
        long rowsRead   = 0;

        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            lineNumber++;
            rowsRead++;

            if (rowsRead > MaxRows)
                yield break;

            // Build the raw CSV line for error reporting
            var rawLine = csv.Parser.RawRecord?.TrimEnd('\r', '\n') ?? "";

            var row = new object?[activeMappings.Length];
            bool hasError = false;

            for (int i = 0; i < activeMappings.Length; i++)
            {
                var m = activeMappings[i];
                string? raw;
                try
                {
                    raw = csv.GetField(m.CsvIndex);
                }
                catch
                {
                    raw = null;
                }

                // Apply null handling
                object? value;
                if (IsNull(raw, request.NullHandling))
                {
                    if (!m.TargetIsNullable)
                    {
                        onError(new RowError(lineNumber, rawLine,
                            $"Column '{m.TargetColumn}': NULL not allowed (column is NOT NULL)."));
                        hasError = true;
                        break;
                    }
                    value = null;
                }
                else
                {
                    try
                    {
                        value = ConvertCell(raw ?? "", m.TargetDataType, culture);
                    }
                    catch (Exception ex)
                    {
                        onError(new RowError(lineNumber, rawLine,
                            $"Column '{m.TargetColumn}' (index {m.CsvIndex}): {ex.Message}"));
                        hasError = true;
                        break;
                    }
                }

                row[i] = value;
            }

            if (!hasError)
                yield return new ProducerRow(row, lineNumber);
        }
    }

    // ── Null detection ────────────────────────────────────────────────────────

    private static bool IsNull(string? raw, NullHandling handling) => handling switch
    {
        NullHandling.EmptyAsNull        => string.IsNullOrEmpty(raw),
        NullHandling.EmptyAsEmptyString => raw is null,
        NullHandling.LiteralNullToken   => raw is null
                                          || string.Equals(raw, "NULL",  StringComparison.OrdinalIgnoreCase)
                                          || raw == @"\N",
        _ => string.IsNullOrEmpty(raw),
    };

    // ── Type conversion ───────────────────────────────────────────────────────

    private static object? ConvertCell(string raw, string dataType, CultureInfo culture)
    {
        var dt = dataType.ToLowerInvariant();
        return dt switch
        {
            // Integer family
            "int" or "integer" or "int4"
                => int.Parse(raw, NumberStyles.Integer, culture),
            "bigint" or "int8"
                => long.Parse(raw, NumberStyles.Integer, culture),
            "smallint" or "int2"
                => short.Parse(raw, NumberStyles.Integer, culture),
            "tinyint"
                => byte.Parse(raw, NumberStyles.Integer, culture),

            // Boolean
            "bit" or "boolean" or "bool"
                => ParseBool(raw),

            // Decimal / money
            "decimal" or "numeric" or "money" or "smallmoney"
                => decimal.Parse(raw, NumberStyles.Number, culture),

            // Floating point
            "float" or "float8" or "double precision" or "double"
                => double.Parse(raw, NumberStyles.Float, culture),
            "real" or "float4"
                => float.Parse(raw, NumberStyles.Float, culture),

            // String types — pass through as-is
            "char" or "character" or "nchar"
            or "varchar" or "character varying" or "nvarchar"
            or "text" or "ntext"
            or "json" or "jsonb" or "xml"
            or "citext"
                => raw,

            // Date / time
            "date"
                => ParseDate(raw, culture),
            "datetime" or "datetime2" or "smalldatetime"
            or "timestamp" or "timestamp without time zone"
                => ParseDateTime(raw, culture),
            "datetimeoffset" or "timestamp with time zone" or "timestamptz"
                => DateTimeOffset.Parse(raw, culture, DateTimeStyles.RoundtripKind),
            "time" or "time without time zone"
                => TimeSpan.Parse(raw, culture),

            // GUID
            "uniqueidentifier" or "uuid"
                => Guid.Parse(raw),

            // Binary: accept 0x... prefix (SSMS-style) or pure hex
            "binary" or "varbinary" or "image" or "bytea"
                => ParseHex(raw),

            // All unrecognised types: pass through as string
            _ => raw,
        };
    }

    private static bool ParseBool(string raw) =>
        raw.Trim().ToLowerInvariant() switch
        {
            "true"  or "1" or "yes" or "on"  => true,
            "false" or "0" or "no"  or "off" => false,
            _ => throw new FormatException(
                $"Cannot parse '{raw}' as boolean. Expected: true/false, 1/0, yes/no."),
        };

    private static DateTime ParseDate(string raw, CultureInfo culture)
    {
        // Try ISO 8601 first (locale-neutral), then culture-specific
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var isoDate))
            return isoDate;
        return DateTime.Parse(raw, culture, DateTimeStyles.None);
    }

    private static DateTime ParseDateTime(string raw, CultureInfo culture)
    {
        // ISO 8601 patterns first
        var isoFormats = new[]
        {
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            "yyyy-MM-ddTHH:mm:ss.fffffff",
            "yyyy-MM-ddTHH:mm:ssZ",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd",
        };
        if (DateTime.TryParseExact(raw, isoFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt))
            return dt;
        return DateTime.Parse(raw, culture, DateTimeStyles.RoundtripKind);
    }

    // Binary: accepts "0x..." or plain hex. Document: hex chosen over base64 as more natural for SQL users.
    private static byte[] ParseHex(string hex)
    {
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];
        if (hex.Length == 0) return Array.Empty<byte>();
        if (hex.Length % 2 != 0)
            throw new FormatException("Hex string must have an even number of digits.");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}

internal record struct ProducerRow(object?[] Values, long LineNumber);
