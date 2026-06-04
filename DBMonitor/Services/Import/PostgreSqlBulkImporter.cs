using Npgsql;
using NpgsqlTypes;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

namespace DBMonitor.Services.Import;

/// <summary>
/// Bulk-imports CSV data into PostgreSQL using Npgsql binary COPY.
///
/// Binary COPY is all-or-nothing per call: a constraint violation inside the COPY stream
/// aborts the entire COPY operation and rolls back the transaction. Our row-level error
/// handling (CsvRowProducer) only protects against parse failures before the row enters
/// the COPY stream — once written, the server controls the rest. This is surfaced clearly
/// in the ImportResult.Message.
/// </summary>
public class PostgreSqlBulkImporter : IBulkImporter
{
    private const long MaxRows   = 5_000_000;
    private const int  MaxErrors = 100;

    public async Task<ImportResult> ImportAsync(
        DbConnection conn, ImportRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var pgConn = (NpgsqlConnection)conn;

        // Re-fetch live column list from catalog to validate mappings
        var liveColumns = await FetchLiveColumnsAsync(pgConn, request.Schema, request.Table, ct);
        var liveDict = liveColumns.ToDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase);

        var activeMappings = request.Mappings
            .Where(m => !m.Skip)
            .ToArray();

        foreach (var m in activeMappings)
        {
            if (!liveDict.ContainsKey(m.TargetColumn))
                throw new InvalidOperationException(
                    $"Column '{m.TargetColumn}' does not exist in {request.Schema}.{request.Table}.");
        }

        // Build the NpgsqlDbType array for each active column (used in binary COPY writes)
        var npgsqlTypes = activeMappings
            .Select(m => liveDict.TryGetValue(m.TargetColumn, out var dt) ? MapToNpgsqlType(dt) : NpgsqlDbType.Text)
            .ToArray();

        var culture    = ResolveCulture(request.CultureName);
        var errors     = new List<RowError>();
        long rowsRead  = 0;
        long rowsRej   = 0;
        long rowsIns   = 0;
        bool truncated = false;
        bool rolledBack = false;

        var quotedSchema = $"\"{request.Schema}\"";
        var quotedTable  = $"\"{request.Table}\"";
        var quotedCols   = string.Join(", ", activeMappings.Select(m => $"\"{m.TargetColumn}\""));
        var copyCmd      = $"COPY {quotedSchema}.{quotedTable} ({quotedCols}) FROM STDIN (FORMAT BINARY)";

        await using var tx = await pgConn.BeginTransactionAsync(ct);
        try
        {
            if (request.ExistingDataMode == ExistingDataMode.TruncateThenInsert)
            {
                await using var delCmd = pgConn.CreateCommand();
                delCmd.Transaction = tx;
                delCmd.CommandText = $"DELETE FROM {quotedSchema}.{quotedTable}";
                delCmd.CommandTimeout = 300;
                await delCmd.ExecuteNonQueryAsync(ct);
                truncated = true;
            }

            await using var writer = await pgConn.BeginBinaryImportAsync(copyCmd, ct);

            await foreach (var produced in CsvRowProducer.ProduceAsync(
                request, culture,
                err =>
                {
                    rowsRej++;
                    if (errors.Count < MaxErrors) errors.Add(err);
                }, ct))
            {
                rowsRead++;
                await writer.StartRowAsync(ct);

                for (int i = 0; i < activeMappings.Length; i++)
                {
                    if (produced.Values[i] is null)
                        await writer.WriteNullAsync(ct);
                    else
                        await WriteValueAsync(writer, produced.Values[i]!, npgsqlTypes[i], ct);
                }
            }

            rowsIns = (long)await writer.CompleteAsync(ct);

            if (request.AbortOnAnyError && rowsRej > 0)
            {
                await tx.RollbackAsync(ct);
                rolledBack = true;
                return new ImportResult(
                    rowsRead, 0, rowsRej, sw.ElapsedMilliseconds, errors,
                    truncated, rolledBack,
                    $"Rolled back: {rowsRej} row(s) rejected and AbortOnAnyError was set.");
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { }
            rolledBack = true;

            var msg = ex is PostgresException pgEx
                ? $"PostgreSQL error during binary COPY (the server rejected the operation): {pgEx.MessageText}. " +
                  "Binary COPY is all-or-nothing; a constraint violation aborts the entire COPY stream."
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
                      "Constraint violations inside binary COPY itself still abort the entire operation.";
        }

        return new ImportResult(
            rowsRead, rowsIns, rowsRej, sw.ElapsedMilliseconds, errors,
            truncated, rolledBack, message);
    }

    // ── NpgsqlDbType mapping ──────────────────────────────────────────────────

    internal static NpgsqlDbType MapToNpgsqlType(string pgType) =>
        pgType.ToLowerInvariant() switch
        {
            "integer" or "int4" or "int"                          => NpgsqlDbType.Integer,
            "bigint" or "int8"                                    => NpgsqlDbType.Bigint,
            "smallint" or "int2"                                  => NpgsqlDbType.Smallint,
            "boolean" or "bool"                                   => NpgsqlDbType.Boolean,
            "numeric" or "decimal"                                => NpgsqlDbType.Numeric,
            "real" or "float4"                                    => NpgsqlDbType.Real,
            "double precision" or "float8" or "double"            => NpgsqlDbType.Double,
            "character varying" or "varchar"                      => NpgsqlDbType.Varchar,
            "text" or "citext"                                    => NpgsqlDbType.Text,
            "character" or "char" or "bpchar"                     => NpgsqlDbType.Char,
            "date"                                                => NpgsqlDbType.Date,
            "timestamp" or "timestamp without time zone"          => NpgsqlDbType.Timestamp,
            "timestamp with time zone" or "timestamptz"           => NpgsqlDbType.TimestampTz,
            "time" or "time without time zone"                    => NpgsqlDbType.Time,
            "uuid"                                                => NpgsqlDbType.Uuid,
            "bytea"                                               => NpgsqlDbType.Bytea,
            "json"                                                => NpgsqlDbType.Json,
            "jsonb"                                               => NpgsqlDbType.Jsonb,
            "xml"                                                 => NpgsqlDbType.Xml,
            _                                                     => NpgsqlDbType.Text,
        };

    private static async Task WriteValueAsync(
        NpgsqlBinaryImporter writer, object value, NpgsqlDbType npgsqlType, CancellationToken ct)
    {
        switch (value)
        {
            case int    v: await writer.WriteAsync(v, npgsqlType, ct);    break;
            case long   v: await writer.WriteAsync(v, npgsqlType, ct);    break;
            case short  v: await writer.WriteAsync(v, npgsqlType, ct);    break;
            case byte   v: await writer.WriteAsync((short)v, NpgsqlDbType.Smallint, ct); break;
            case bool   v: await writer.WriteAsync(v, npgsqlType, ct);    break;
            case decimal v: await writer.WriteAsync(v, npgsqlType, ct);   break;
            case double  v: await writer.WriteAsync(v, npgsqlType, ct);   break;
            case float   v: await writer.WriteAsync(v, npgsqlType, ct);   break;
            case string  v: await writer.WriteAsync(v, npgsqlType, ct);   break;
            case DateTime v: await writer.WriteAsync(v, npgsqlType, ct);  break;
            case DateTimeOffset v: await writer.WriteAsync(v, npgsqlType, ct); break;
            case TimeSpan v: await writer.WriteAsync(v, npgsqlType, ct);  break;
            case Guid v:    await writer.WriteAsync(v, npgsqlType, ct);   break;
            case byte[] v:  await writer.WriteAsync(v, npgsqlType, ct);   break;
            default:        await writer.WriteAsync(value.ToString() ?? "", NpgsqlDbType.Text, ct); break;
        }
    }

    // ── Catalog re-fetch ──────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<(string Name, string DataType)>> FetchLiveColumnsAsync(
        NpgsqlConnection conn, string schema, string table, CancellationToken ct)
    {
        var results = new List<(string, string)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position
            """;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table",  table);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    private static CultureInfo ResolveCulture(string name) =>
        string.IsNullOrEmpty(name) || name.Equals("invariant", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(name);
}
