using System.Data.Common;
using System.Diagnostics;

namespace DBMonitor.Services.Query;

/// <summary>
/// SQL Server implementation.  Identifier injection is prevented by:
///   1. Round-tripping schema+table through INFORMATION_SCHEMA before query construction.
///   2. Validating every OrderBy column and every filter column against the live column list.
///   3. Quoting all identifiers with [bracket] notation (] doubled) via Quote().
///   4. Mapping ORDER BY direction to a fixed "ASC"/"DESC" constant — never echoing user input.
///   5. All filter VALUES go through DbParameter; identifiers never do.
/// </summary>
public class SqlServerTableDataReader : ITableDataReader
{
    private readonly DbConnection _conn;

    public SqlServerTableDataReader(DbConnection conn) => _conn = conn;

    public async Task<TableDataPage> ReadPageAsync(
        string schema, string table, TableQuery query, CancellationToken ct = default)
    {
        // ── 1. Validate schema+table; obtain authoritative column list from catalog ──
        var columns = await FetchColumnsFromCatalogAsync(schema, table, ct);
        if (columns.Count == 0)
            throw new InvalidOperationException(
                $"Table [{schema}].[{table}] has no columns or does not exist in this database.");

        // ── 2. Validate and resolve ORDER BY column ──────────────────────────────
        var orderByCol = ResolveOrderBy(query.OrderByColumn, columns);
        var direction  = query.Descending ? "DESC" : "ASC"; // fixed constant, never user input

        // ── 3. Validate filter columns ───────────────────────────────────────────
        ValidateFilterColumns(query.Filters, columns);

        // ── 4. Clamp pagination ──────────────────────────────────────────────────
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 50 : query.PageSize, 1, 500);
        var page     = Math.Max(1, query.Page);
        var skip     = (long)(page - 1) * pageSize;

        // ── 5. Build WHERE clause with parameterised values ──────────────────────
        var (whereSql, whereParams) = WhereClauseBuilder.Build(
            query.Filters,
            quoter:      Quote,
            likeEscaper: EscapeLike,
            likeSuffix:  "");  // SQL Server uses [bracket] LIKE escaping, no ESCAPE clause needed

        // ── 6. Quote schema/table/orderBy now that they are validated ────────────
        var qSchema  = Quote(schema);
        var qTable   = Quote(table);
        var qOrderBy = Quote(orderByCol);
        var colList  = string.Join(", ", columns.Select(c => Quote(c.Name)));

        var sw = Stopwatch.StartNew();

        // ── 7. COUNT query ───────────────────────────────────────────────────────
        long totalCount;
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT_BIG(*) FROM {qSchema}.{qTable}{whereSql}";
            WhereClauseBuilder.ApplyParams(cmd, whereParams);
            totalCount = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        // ── 8. Data query ────────────────────────────────────────────────────────
        var rows = new List<IReadOnlyList<object?>>();
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT {colList}" +
                $"\nFROM {qSchema}.{qTable}" +
                whereSql +
                $"\nORDER BY {qOrderBy} {direction}" +
                $"\nOFFSET @__skip ROWS FETCH NEXT @__take ROWS ONLY";

            WhereClauseBuilder.ApplyParams(cmd, whereParams);
            WhereClauseBuilder.AddParam(cmd, "@__skip", skip);
            WhereClauseBuilder.AddParam(cmd, "@__take", pageSize);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(RowProjector.ProjectRow(reader));
        }

        sw.Stop();
        return new TableDataPage(columns, rows, totalCount, page, pageSize, sw.ElapsedMilliseconds);
    }

    // ── Catalog validation ────────────────────────────────────────────────────
    // Schema and table names are passed as SQL *parameters* in the WHERE clause
    // of this catalog query — they are never embedded as identifiers here.
    // Only after this returns successfully do we embed them (after quoting) in
    // the actual data query.

    private async Task<IReadOnlyList<ColumnDescriptor>> FetchColumnsFromCatalogAsync(
        string schema, string table, CancellationToken ct)
    {
        var results = new List<ColumnDescriptor>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME, DATA_TYPE, ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """;
        WhereClauseBuilder.AddParam(cmd, "@schema", schema);
        WhereClauseBuilder.AddParam(cmd, "@table",  table);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new ColumnDescriptor(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return results;
    }

    // ── Identifier validation (against the catalog-sourced column list) ───────

    private static string ResolveOrderBy(string? requested, IReadOnlyList<ColumnDescriptor> columns)
    {
        if (string.IsNullOrEmpty(requested))
            return columns[0].Name; // safe default: first column from the catalog

        var match = columns.FirstOrDefault(
            c => string.Equals(c.Name, requested, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new InvalidOperationException(
                $"ORDER BY column '{requested}' does not exist in this table.");

        return match.Name; // return catalog-correct casing, not what the caller sent
    }

    private static void ValidateFilterColumns(
        IReadOnlyList<ColumnFilter> filters, IReadOnlyList<ColumnDescriptor> columns)
    {
        foreach (var f in filters)
        {
            var exists = columns.Any(
                c => string.Equals(c.Name, f.Column, StringComparison.OrdinalIgnoreCase));
            if (!exists)
                throw new InvalidOperationException(
                    $"Filter column '{f.Column}' does not exist in this table.");
        }
    }

    // ── Identifier quoting (SQL Server: [name], ] doubled) ───────────────────

    private static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]") + "]";

    // ── LIKE value escaping (SQL Server bracket notation, no ESCAPE clause) ──

    private static string EscapeLike(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}
