using DBMonitor.Services.Schema;
using System.Data;
using System.Data.Common;
using System.Diagnostics;

namespace DBMonitor.Services.Query;

public class PostgreSqlProcedureExecutor : IProcedureExecutor
{
    private const int MaxTotalCells = 200_000;

    public async Task<ProcedureExecution> ExecuteAsync(DbConnection conn, ProcedureRequest request, CancellationToken ct)
    {
        var timeout = Math.Clamp(request.TimeoutSeconds ?? 30, 1, 300);
        var maxRows  = Math.Clamp(request.MaxRows       ?? 1_000, 1, 10_000);

        // Re-fetch parameter metadata — same rationale as SQL Server executor.
        var catalogParams = await FetchCatalogParamsAsync(conn, request.Schema, request.Name, ct);

        var formLookup = request.Parameters.ToDictionary(
            p => p.Name.TrimStart('@').ToLowerInvariant(),
            p => p,
            StringComparer.OrdinalIgnoreCase);

        foreach (var cp in catalogParams)
        {
            if (cp.Direction == ParameterDirection.Output) continue;
            var norm = cp.Name.ToLowerInvariant();
            if (formLookup.TryGetValue(norm, out var fv) && fv.UseDefault) continue;
            if (!cp.HasDefault && !formLookup.ContainsKey(norm))
                throw new InvalidOperationException(
                    $"Parameter {cp.Name} has no default value and was not supplied.");
        }

        var sw = Stopwatch.StartNew();
        var resultSets   = new List<QueryResultSet>();
        var outputValues = new Dictionary<string, object?>();
        int recordsAffected = -1;

        await using var cmd = conn.CreateCommand();
        // PostgreSQL: Npgsql generates CALL schema.name(...) when CommandType.StoredProcedure.
        // This executor is scoped to PROCEDURE only; functions are not executed here.
        cmd.CommandText    = $"\"{request.Schema.Replace("\"", "\"\"")}\".\"{request.Name.Replace("\"", "\"\"")}\"";
        cmd.CommandType    = CommandType.StoredProcedure;
        cmd.CommandTimeout = timeout;

        foreach (var cp in catalogParams)
        {
            var norm = cp.Name.ToLowerInvariant();
            if (formLookup.TryGetValue(norm, out var fv) && fv.UseDefault) continue;

            var dbType = SqlTypeMapper.MapToDbType(cp.DataType);
            var dbp    = cmd.CreateParameter();
            dbp.ParameterName = cp.Name;
            dbp.DbType        = dbType;
            dbp.Direction     = cp.Direction;

            if (dbType == DbType.Decimal && cp.Precision.HasValue)
                SetPrecisionScale(dbp, cp.Precision.Value, cp.Scale ?? 0);
            if (cp.MaxLength.HasValue)
                dbp.Size = cp.MaxLength.Value;

            if (cp.Direction == ParameterDirection.Output)
                dbp.Value = DBNull.Value;
            else if (fv is null || fv.IsNull || string.IsNullOrEmpty(fv.RawValue))
                dbp.Value = DBNull.Value;
            else
                dbp.Value = SqlTypeMapper.ParseRawValue(fv.RawValue, dbType, cp.Name);

            cmd.Parameters.Add(dbp);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        int  totalCells = 0;
        bool hitCellCap = false;

        do
        {
            if (reader.FieldCount == 0) continue;

            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(i => new ColumnDescriptor(reader.GetName(i), reader.GetFieldType(i).Name, i))
                .ToList();

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
        sw.Stop();

        foreach (DbParameter p in cmd.Parameters)
        {
            if (p.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
                outputValues[p.ParameterName] = RowProjector.ProjectValue(p.Value);
        }

        // PostgreSQL has no SQL Server-style integer RETURN VALUE concept.
        return new ProcedureExecution(resultSets, null, outputValues, recordsAffected, sw.ElapsedMilliseconds, null);
    }

    private static async Task<IReadOnlyList<RoutineParameter>> FetchCatalogParamsAsync(
        DbConnection conn, string schema, string name, CancellationToken ct)
    {
        var results = new List<RoutineParameter>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                par.parameter_name,
                par.data_type,
                par.parameter_mode,
                par.parameter_default IS NOT NULL AS has_default,
                par.character_maximum_length,
                par.numeric_precision,
                par.numeric_scale
            FROM information_schema.routines r
            JOIN information_schema.parameters par
                ON  par.specific_catalog = r.specific_catalog
                AND par.specific_schema  = r.specific_schema
                AND par.specific_name    = r.specific_name
            WHERE r.routine_schema = @schema
              AND r.routine_name   = @name
            ORDER BY par.ordinal_position
            LIMIT 500
            """;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@name",   name);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RoutineParameter(
                Name:       reader.IsDBNull(0) ? "" : reader.GetString(0),
                DataType:   reader.GetString(1),
                Direction:  ParseDir(reader.IsDBNull(2) ? "IN" : reader.GetString(2)),
                HasDefault: reader.GetBoolean(3),
                MaxLength:  reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Precision:  reader.IsDBNull(5) ? null : (byte?)reader.GetInt32(5),
                Scale:      reader.IsDBNull(6) ? null : (byte?)reader.GetInt32(6),
                IsNullable: true)); // PostgreSQL params always accept NULL
        }
        return results;
    }

    private static ParameterDirection ParseDir(string mode) => mode.ToUpperInvariant() switch
    {
        "OUT"   => ParameterDirection.Output,
        "INOUT" => ParameterDirection.InputOutput,
        _       => ParameterDirection.Input,
    };

    private static void SetPrecisionScale(DbParameter p, byte precision, byte scale)
    {
        var t = p.GetType();
        t.GetProperty("Precision")?.SetValue(p, precision);
        t.GetProperty("Scale")?.SetValue(p, scale);
    }

    private static void AddParam(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
