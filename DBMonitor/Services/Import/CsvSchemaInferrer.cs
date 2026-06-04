using CsvHelper;
using CsvHelper.Configuration;
using DBMonitor.Models;
using System.Globalization;
using System.Text;

namespace DBMonitor.Services.Import;

public record InferredColumn(
    string Name,
    int    CsvIndex,
    string SqlServerType,
    string PostgreSqlType,
    bool   IsNullable);

public class CsvSchemaInferrer
{
    private const int SampleLimit = 2_000;

    public async Task<IReadOnlyList<InferredColumn>> InferAsync(
        string tempFilePath,
        char   delimiter,
        Encoding encoding,
        bool   hasHeader,
        CancellationToken ct = default)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter       = delimiter.ToString(),
            HasHeaderRecord = hasHeader,
            BadDataFound    = null,
            MissingFieldFound = null,
        };

        string[] headers;
        var rows = new List<string[]>();

        using var reader = new StreamReader(tempFilePath, encoding, detectEncodingFromByteOrderMarks: true);
        using var csv    = new CsvReader(reader, config);

        if (hasHeader)
        {
            await csv.ReadAsync();
            csv.ReadHeader();
            headers = csv.HeaderRecord ?? [];
        }
        else
        {
            // Peek first row to get column count
            if (!await csv.ReadAsync()) return [];
            var first = new List<string>();
            for (int i = 0; csv.TryGetField<string>(i, out var v); i++) first.Add(v ?? "");
            rows.Add(first.ToArray());
            headers = Enumerable.Range(0, first.Count).Select(i => "Column" + (i + 1)).ToArray();
        }

        int rowsSampled = 0;
        while (rowsSampled < SampleLimit && await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            var row = new string[headers.Length];
            for (int i = 0; i < headers.Length; i++)
                row[i] = csv.TryGetField<string>(i, out var v) ? (v ?? "") : "";
            rows.Add(row);
            rowsSampled++;
        }

        return headers.Select((hdr, idx) => InferColumn(hdr, idx, rows)).ToList();
    }

    private static InferredColumn InferColumn(string rawName, int idx, List<string[]> rows)
    {
        var name = SanitizeName(rawName, idx);

        bool hasNull     = false;
        bool hasAnyValue = false;
        bool allInt      = true;
        bool allBigInt   = true;
        bool allDecimal  = true;
        bool allBool     = true;
        bool allDate     = true;
        bool allDateTime = true;

        int  maxLen      = 0;
        int  maxPrec     = 0;
        int  maxScale    = 0;

        foreach (var row in rows)
        {
            var raw = idx < row.Length ? row[idx] : "";
            if (string.IsNullOrWhiteSpace(raw)) { hasNull = true; continue; }

            hasAnyValue = true;
            var s = raw.Trim();
            maxLen = Math.Max(maxLen, s.Length);

            // integer (int32)?
            if (allInt && !int.TryParse(s, out _)) allInt = false;

            // bigint?
            if (allBigInt && !long.TryParse(s, out _)) allBigInt = false;

            // decimal?
            if (allDecimal && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
            {
                var parts = s.TrimStart('-').Split('.');
                int wholePart = parts[0].Length;
                int scalePart = parts.Length > 1 ? parts[1].Length : 0;
                maxScale = Math.Max(maxScale, scalePart);
                maxPrec  = Math.Max(maxPrec, wholePart + scalePart);
            }
            else if (allDecimal)
            {
                allDecimal = false;
            }

            // bool?
            if (allBool)
            {
                var low = s.ToLowerInvariant();
                if (low != "true" && low != "false" && low != "1" && low != "0" &&
                    low != "yes"  && low != "no")
                    allBool = false;
            }

            // date?
            if (allDate && !DateOnly.TryParse(s, out _)) allDate = false;

            // datetime?
            if (allDateTime && !DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out _))
                allDateTime = false;
        }

        if (!hasAnyValue)
            return new InferredColumn(name, idx, "NVARCHAR(50)", "VARCHAR(50)", true);

        // Numeric types beat bool (1/0 is more likely a number than a flag)
        bool resolved = false;
        string ssType = "", pgType = "";

        if (allInt)
        {
            ssType = "INT"; pgType = "INTEGER"; resolved = true;
        }
        else if (allBigInt)
        {
            ssType = "BIGINT"; pgType = "BIGINT"; resolved = true;
        }
        else if (allDecimal && maxScale > 0)
        {
            int p = Math.Min(maxPrec + 2, 38);
            int s = Math.Min(maxScale, 10);
            ssType = $"DECIMAL({p},{s})"; pgType = $"NUMERIC({p},{s})"; resolved = true;
        }
        else if (allBool)
        {
            ssType = "BIT"; pgType = "BOOLEAN"; resolved = true;
        }
        else if (allDate)
        {
            ssType = "DATE"; pgType = "DATE"; resolved = true;
        }
        else if (allDateTime)
        {
            ssType = "DATETIME2(0)"; pgType = "TIMESTAMP"; resolved = true;
        }

        if (!resolved)
        {
            int bucket = BucketLength(maxLen);
            ssType = bucket <= 4000 ? $"NVARCHAR({bucket})" : "NVARCHAR(MAX)";
            pgType = bucket <= 1000 ? $"VARCHAR({bucket})"  : "TEXT";
        }

        return new InferredColumn(name, idx, ssType, pgType, hasNull);
    }

    public static string BuildCreateTableSql(
        IReadOnlyList<InferredColumn> cols,
        DbProviderKind provider,
        string schema,
        string table)
    {
        var sb = new StringBuilder();

        if (provider == DbProviderKind.SqlServer)
        {
            var schemaQ = $"[{schema}]";
            var tableQ  = $"[{table}]";
            sb.AppendLine($"CREATE TABLE {schemaQ}.{tableQ} (");
            for (int i = 0; i < cols.Count; i++)
            {
                var c    = cols[i];
                var null_ = c.IsNullable ? "NULL" : "NOT NULL";
                var comma = i < cols.Count - 1 ? "," : "";
                sb.AppendLine($"    [{c.Name}] {c.SqlServerType} {null_}{comma}");
            }
            sb.Append(");");
        }
        else // PostgreSQL
        {
            var schemaQ = $"\"{schema}\"";
            var tableQ  = $"\"{table}\"";
            sb.AppendLine($"CREATE TABLE {schemaQ}.{tableQ} (");
            for (int i = 0; i < cols.Count; i++)
            {
                var c     = cols[i];
                var null_ = c.IsNullable ? "" : " NOT NULL";
                var comma = i < cols.Count - 1 ? "," : "";
                sb.AppendLine($"    \"{c.Name}\" {c.PostgreSqlType}{null_}{comma}");
            }
            sb.Append(");");
        }

        return sb.ToString();
    }

    private static string SanitizeName(string raw, int idx)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Column" + (idx + 1);
        // Keep letters, digits, underscores; replace everything else with underscore
        var sb = new StringBuilder(raw.Trim());
        for (int i = 0; i < sb.Length; i++)
        {
            char c = sb[i];
            if (!char.IsLetterOrDigit(c) && c != '_') sb[i] = '_';
        }
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static int BucketLength(int len) => len switch
    {
        <= 10   => 10,
        <= 25   => 25,
        <= 50   => 50,
        <= 100  => 100,
        <= 200  => 200,
        <= 500  => 500,
        <= 1000 => 1000,
        <= 4000 => 4000,
        _       => int.MaxValue, // signals MAX / TEXT
    };
}
