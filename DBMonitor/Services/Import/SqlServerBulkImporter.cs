using Microsoft.Data.SqlClient;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DBMonitor.Services.Import;

/// <summary>
/// Bulk-imports CSV data into SQL Server using SqlBulkCopy.
///
/// Constraint behaviour: SqlBulkCopyOptions.CheckConstraints causes the server to enforce
/// FK / CHECK / UNIQUE constraints after each batch lands. A constraint violation inside
/// SqlBulkCopy aborts the entire copy operation and rolls back the transaction regardless
/// of our row-level error handling, because we can only intercept parse failures BEFORE
/// a row enters the COPY stream. This is surfaced clearly in the ImportResult.Message.
/// </summary>
public class SqlServerBulkImporter : IBulkImporter
{
    private const long MaxRows    = 5_000_000;
    private const int  MaxErrors  = 100;

    public async Task<ImportResult> ImportAsync(
        DbConnection conn, ImportRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Clamp batch size
        int batchSize = Math.Clamp(request.BatchSize, 100, 100_000);

        // Re-fetch live column list to validate mappings against catalog (not client-posted data)
        var liveColumns = await FetchLiveColumnsAsync((SqlConnection)conn, request.Schema, request.Table, ct);
        var liveColNames = new HashSet<string>(liveColumns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        var activeMappings = request.Mappings
            .Where(m => !m.Skip)
            .ToArray();

        // Validate every mapped target column exists in the live catalog
        foreach (var m in activeMappings)
        {
            if (!liveColNames.Contains(m.TargetColumn))
                throw new InvalidOperationException(
                    $"Column '{m.TargetColumn}' does not exist in {request.Schema}.{request.Table}.");
        }

        var culture = ResolveCulture(request.CultureName);

        var errors      = new List<RowError>();
        long rowsRead   = 0;
        long rowsRej    = 0;
        long rowsIns    = 0;
        bool truncated  = false;
        bool rolledBack = false;

        var quotedTable = $"[{request.Schema}].[{request.Table}]";

        // Build DataTable schema for bulk copy
        var dt = BuildDataTable(activeMappings);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // Truncate via DELETE (not TRUNCATE TABLE — see spec for rationale)
            if (request.ExistingDataMode == ExistingDataMode.TruncateThenInsert)
            {
                await using var delCmd = conn.CreateCommand();
                delCmd.Transaction = tx;
                delCmd.CommandText = $"DELETE FROM {quotedTable}";
                delCmd.CommandTimeout = 300;
                await delCmd.ExecuteNonQueryAsync(ct);
                truncated = true;
            }

            using var bulkCopy = new SqlBulkCopy(
                (SqlConnection)conn,
                SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.KeepNulls,
                tx)
            {
                DestinationTableName = quotedTable,
                BatchSize            = batchSize,
                BulkCopyTimeout      = 600,
            };

            // Map columns by name (raw, not quoted — SqlBulkCopy takes raw names)
            foreach (var m in activeMappings)
                bulkCopy.ColumnMappings.Add(m.TargetColumn, m.TargetColumn);

            // Stream CSV rows into batches
            await foreach (var produced in CsvRowProducer.ProduceAsync(
                request, culture,
                err =>
                {
                    rowsRej++;
                    if (errors.Count < MaxErrors) errors.Add(err);
                }, ct))
            {
                rowsRead++;

                var dataRow = dt.NewRow();
                for (int i = 0; i < activeMappings.Length; i++)
                    dataRow[i] = produced.Values[i] ?? DBNull.Value;
                dt.Rows.Add(dataRow);

                if (dt.Rows.Count >= batchSize)
                {
                    await bulkCopy.WriteToServerAsync(dt, ct);
                    rowsIns += dt.Rows.Count;
                    dt.Clear();
                }
            }

            // Flush remaining rows
            if (dt.Rows.Count > 0)
            {
                await bulkCopy.WriteToServerAsync(dt, ct);
                rowsIns += dt.Rows.Count;
                dt.Clear();
            }

            // Roll back if AbortOnAnyError and there were rejected rows
            if (request.AbortOnAnyError && rowsRej > 0)
            {
                await tx.RollbackAsync(ct);
                rolledBack = true;
                rowsIns = 0;
                return new ImportResult(
                    rowsRead, rowsIns, rowsRej, sw.ElapsedMilliseconds, errors,
                    truncated, rolledBack,
                    $"Rolled back: {rowsRej} row(s) rejected and AbortOnAnyError was set.");
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { }
            rolledBack = true;
            rowsIns    = 0;

            // Surface SqlBulkCopy constraint violation clearly
            var msg = ex is SqlException sqlex && sqlex.Number is 2627 or 547 or 2601
                ? $"Constraint violation during bulk copy (the server rejected a batch): {ex.Message}. " +
                  "SqlBulkCopy constraint failures abort the entire operation regardless of per-row error handling."
                : ex.Message;

            return new ImportResult(
                rowsRead, 0, rowsRej, sw.ElapsedMilliseconds, errors,
                truncated, rolledBack, msg);
        }

        string? message = null;
        if (rowsRej > 0)
        {
            var firstErr = errors.Count > 0 ? $" (first error: {errors[0].Error})" : "";
            message = $"{rowsRej} row(s) rejected{firstErr}. " +
                      "Constraint violations inside SqlBulkCopy itself still abort the entire batch.";
        }

        return new ImportResult(
            rowsRead, rowsIns, rowsRej, sw.ElapsedMilliseconds, errors,
            truncated, rolledBack, message);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<(string Name, string DataType)>> FetchLiveColumnsAsync(
        SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        var results = new List<(string, string)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.name, ty.name
            FROM sys.columns c
            JOIN sys.types ty  ON ty.user_type_id = c.user_type_id
            JOIN sys.objects o ON o.object_id      = c.object_id
            JOIN sys.schemas s ON s.schema_id      = o.schema_id
            WHERE s.name = @schema AND o.name = @table
            ORDER BY c.column_id
            """;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table",  table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    private static DataTable BuildDataTable(ColumnMapping[] mappings)
    {
        var dt = new DataTable();
        foreach (var m in mappings)
            dt.Columns.Add(m.TargetColumn, typeof(object));
        return dt;
    }

    private static CultureInfo ResolveCulture(string name) =>
        string.IsNullOrEmpty(name) || name.Equals("invariant", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(name);
}
