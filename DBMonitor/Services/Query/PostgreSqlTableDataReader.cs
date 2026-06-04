using System.Data.Common;
using System.Diagnostics;

namespace DBMonitor.Services.Query;

/// <summary>
/// PostgreSQL implementation.  Identifier injection is prevented by the same
/// approach as SqlServerTableDataReader: catalog round-trip → column-list validation
/// → Quote("name") with " doubled → fixed ASC/DESC constant.
/// LIKE values use '!' as the ESCAPE character to avoid backslash ambiguity
/// across different standard_conforming_strings settings.
/// </summary>
public class PostgreSqlTableDataReader : ITableDataReader
{
    private readonly DbConnection _conn;

    public PostgreSqlTableDataReader(DbConnection conn) => _conn = conn;

    public async Task<TableDataPage> ReadPageAsync(
        string schema, string table, TableQuery query, CancellationToken ct = default)
    {
        // ── 1. Validate schema+table; obtain authoritative column list ────────────
        var columns = await FetchColumnsFromCatalogAsync(schema, table, ct);
        if (columns.Count == 0)
            throw new InvalidOperationException(
                $"Table \"{schema}\".\"{table}\" has no columns or does not exist in this database.");

        // ── 2. Validate and resolve ORDER BY column ──────────────────────────────
        var orderByCol = ResolveOrderBy(query.OrderByColumn, columns);
        var direction  = query.Descending ? "DESC" : "ASC";

        // ── 3. Validate filter columns ───────────────────────────────────────────
        ValidateFilterColumns(query.Filters, columns);

        // ── 4. Clamp pagination ──────────────────────────────────────────────────
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 50 : query.PageSize, 1, 500);
        var page     = Math.Max(1, query.Page);
        var skip     = (long)(page - 1) * pageSize;

        // ── 5. Build WHERE clause ────────────────────────────────────────────────
        var (whereSql, whereParams) = WhereClauseBuilder.Build(
            query.Filters,
            quoter:      Quote,
            likeEscaper: EscapeLike,
            likeSuffix:  " ESCAPE '!'"); // '!' chosen to avoid backslash handling differences

        // ── 6. Quote validated identifiers ───────────────────────────────────────
        var qSchema  = Quote(schema);
        var qTable   = Quote(table);
        var qOrderBy = Quote(orderByCol);
        var colList  = string.Join(", ", columns.Select(c => Quote(c.Name)));

        var sw = Stopwatch.StartNew();

        // ── 7. COUNT query ───────────────────────────────────────────────────────
        long totalCount;
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {qSchema}.{qTable}{whereSql}";
            WhereClauseBuilder.ApplyParams(cmd, whereParams);
            totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
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
                $"\nLIMIT @__take OFFSET @__skip";

            WhereClauseBuilder.ApplyParams(cmd, whereParams);
            WhereClauseBuilder.AddParam(cmd, "@__take", pageSize);
            WhereClauseBuilder.AddParam(cmd, "@__skip", skip);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(RowProjector.ProjectRow(reader));
        }

        sw.Stop();
        return new TableDataPage(columns, rows, totalCount, page, pageSize, sw.ElapsedMilliseconds);
    }

    // ── Catalog validation ────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ColumnDescriptor>> FetchColumnsFromCatalogAsync(
        string schema, string table, CancellationToken ct)
    {
        var results = new List<ColumnDescriptor>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT column_name, data_type, ordinal_position
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position
            """;
        WhereClauseBuilder.AddParam(cmd, "@schema", schema);
        WhereClauseBuilder.AddParam(cmd, "@table",  table);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new ColumnDescriptor(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return results;
    }

    // ── Identifier validation ─────────────────────────────────────────────────

    private static string ResolveOrderBy(string? requested, IReadOnlyList<ColumnDescriptor> columns)
    {
        if (string.IsNullOrEmpty(requested))
            return columns[0].Name;

        var match = columns.FirstOrDefault(
            c => string.Equals(c.Name, requested, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new InvalidOperationException(
                $"ORDER BY column '{requested}' does not exist in this table.");

        return match.Name;
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

    // ── Identifier quoting (PostgreSQL: "name", " doubled) ───────────────────

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    // ── LIKE value escaping (! as ESCAPE char) ────────────────────────────────

    private static string EscapeLike(string value) =>
        value.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
}
