using System.Data.Common;
using System.Diagnostics;

namespace DBMonitor.Services.Query;

public class QueryExecutor : IQueryExecutor
{
    private const int MaxTotalCells = 200_000;

    public async Task<QueryExecution> ExecuteAsync(DbConnection conn, QueryRequest request, CancellationToken ct)
    {
        var timeout    = Math.Clamp(request.TimeoutSeconds ?? 30, 1, 300);
        var maxRows    = Math.Clamp(request.MaxRows ?? 1_000, 1, 10_000);
        var resultSets = new List<QueryResultSet>();
        var sw         = Stopwatch.StartNew();

        DbTransaction? tx = null;
        int  recordsAffected = -1;
        bool rolledBack      = false;
        string? message      = null;

        try
        {
            if (!request.AllowDestructive)
                tx = await conn.BeginTransactionAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText    = request.Sql;
            cmd.CommandTimeout = timeout;
            if (tx != null) cmd.Transaction = tx;

            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                int totalCells = 0;
                bool hitCellCap = false;

                do
                {
                    if (reader.FieldCount == 0) continue;

                    var columns = new List<ColumnDescriptor>(reader.FieldCount);
                    for (int i = 0; i < reader.FieldCount; i++)
                        columns.Add(new ColumnDescriptor(reader.GetName(i), reader.GetFieldType(i).Name, i));

                    var rows      = new List<IReadOnlyList<object?>>();
                    bool truncated = false;

                    while (await reader.ReadAsync(ct))
                    {
                        if (rows.Count >= maxRows) { truncated = true; break; }

                        rows.Add(RowProjector.ProjectRow(reader));
                        totalCells += reader.FieldCount;

                        if (totalCells >= MaxTotalCells) { truncated = true; hitCellCap = true; break; }
                    }

                    resultSets.Add(new QueryResultSet(columns, rows, truncated));

                    if (hitCellCap) break;
                }
                while (await reader.NextResultAsync(ct));

                recordsAffected = reader.RecordsAffected;
            } // reader disposed here — must be closed before COMMIT/ROLLBACK on the same connection

            sw.Stop();

            if (tx != null)
            {
                if (recordsAffected > 0)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    rolledBack = true;
                    message = $"Statement modified {recordsAffected} row(s) — rolled back because Allow destructive was not checked.";
                }
                else
                {
                    await tx.CommitAsync(ct);
                }
            }
        }
        catch
        {
            sw.Stop();
            if (tx != null)
            {
                try { await tx.RollbackAsync(CancellationToken.None); } catch { }
            }
            throw;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }

        return new QueryExecution(resultSets, recordsAffected, sw.ElapsedMilliseconds, rolledBack, message);
    }
}
