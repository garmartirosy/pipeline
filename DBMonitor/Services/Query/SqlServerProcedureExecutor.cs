using DBMonitor.Services.Schema;
using System.Data;
using System.Data.Common;
using System.Diagnostics;

namespace DBMonitor.Services.Query;

public class SqlServerProcedureExecutor : IProcedureExecutor
{
    private const int MaxTotalCells = 200_000;

    public async Task<ProcedureExecution> ExecuteAsync(DbConnection conn, ProcedureRequest request, CancellationToken ct)
    {
        var timeout = Math.Clamp(request.TimeoutSeconds ?? 30, 1, 300);
        var maxRows  = Math.Clamp(request.MaxRows       ?? 1_000, 1, 10_000);

        // Re-fetch parameter metadata from the live catalog — don't trust the client-posted list.
        // The form may be stale if the procedure was ALTERed after the page loaded.
        var catalogParams = await FetchCatalogParamsAsync(conn, request.Schema, request.Name, ct);

        var formLookup = request.Parameters.ToDictionary(
            p => NormName(p.Name),
            p => p,
            StringComparer.OrdinalIgnoreCase);

        // Validate: every non-default, non-output IN parameter must have a supplied value.
        foreach (var cp in catalogParams)
        {
            if (cp.Direction == ParameterDirection.Output) continue;
            if (formLookup.TryGetValue(NormName(cp.Name), out var fv) && fv.UseDefault) continue;
            if (!cp.HasDefault && !formLookup.ContainsKey(NormName(cp.Name)))
                throw new InvalidOperationException(
                    $"Parameter {cp.Name} has no default value and was not supplied.");
        }

        var sw = Stopwatch.StartNew();
        var resultSets  = new List<QueryResultSet>();
        var outputValues = new Dictionary<string, object?>();
        int  recordsAffected = -1;
        int? returnValue     = null;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = $"[{request.Schema.Replace("]", "]]")}].[{request.Name.Replace("]", "]]")}]";
        cmd.CommandType    = CommandType.StoredProcedure;
        cmd.CommandTimeout = timeout;

        // SQL Server stored procedures always have an implicit integer return value.
        var rvParam = cmd.CreateParameter();
        rvParam.ParameterName = "@RETURN_VALUE";
        rvParam.Direction     = ParameterDirection.ReturnValue;
        rvParam.DbType        = DbType.Int32;
        cmd.Parameters.Add(rvParam);

        foreach (var cp in catalogParams)
        {
            if (formLookup.TryGetValue(NormName(cp.Name), out var fv) && fv.UseDefault)
                continue; // omit parameter entirely so the proc applies its DEFAULT

            var dbType = SqlTypeMapper.MapToDbType(cp.DataType);
            var dbp    = cmd.CreateParameter();
            dbp.ParameterName = cp.Name; // already includes @ from sys.parameters
            dbp.DbType        = dbType;
            dbp.Direction     = cp.Direction;

            if (dbType == DbType.Decimal && cp.Precision.HasValue)
                SetPrecisionScale(dbp, cp.Precision.Value, cp.Scale ?? 0);
            if (cp.MaxLength.HasValue)
                dbp.Size = cp.MaxLength.Value; // -1 passes through as MAX

            if (cp.Direction == ParameterDirection.Output)
            {
                dbp.Value = DBNull.Value; // output-only: no input value needed
            }
            else if (fv is null || fv.IsNull || string.IsNullOrEmpty(fv.RawValue))
            {
                dbp.Value = DBNull.Value;
            }
            else
            {
                dbp.Value = SqlTypeMapper.ParseRawValue(fv.RawValue, dbType, cp.Name.TrimStart('@'));
            }

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

        // Read back OUTPUT/INOUT parameter values
        foreach (DbParameter p in cmd.Parameters)
        {
            if (p.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
                outputValues[p.ParameterName.TrimStart('@')] = RowProjector.ProjectValue(p.Value);
        }

        if (rvParam.Value is not DBNull && rvParam.Value is not null)
            returnValue = Convert.ToInt32(rvParam.Value);

        return new ProcedureExecution(resultSets, returnValue, outputValues, recordsAffected, sw.ElapsedMilliseconds, null);
    }

    private static async Task<IReadOnlyList<RoutineParameter>> FetchCatalogParamsAsync(
        DbConnection conn, string schema, string name, CancellationToken ct)
    {
        var results = new List<RoutineParameter>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                p.name,
                ty.name         AS data_type,
                p.is_output,
                p.has_default_value,
                p.max_length,
                p.precision,
                p.scale,
                p.is_nullable
            FROM sys.objects    o
            JOIN sys.schemas    s  ON s.schema_id  = o.schema_id
            LEFT JOIN sys.parameters p  ON p.object_id    = o.object_id
                                       AND p.parameter_id > 0
            LEFT JOIN sys.types      ty ON ty.user_type_id = p.user_type_id
            WHERE s.name = @schema
              AND o.name = @name
            ORDER BY p.parameter_id
            """;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@name",   name);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue; // proc with no params yields one null row via LEFT JOIN

            var dataType  = reader.IsDBNull(1) ? "nvarchar" : reader.GetString(1);
            var rawMaxLen = reader.IsDBNull(4) ? (int?)null : (int)reader.GetInt16(4);
            int? maxLength = rawMaxLen.HasValue
                ? ((dataType is "nvarchar" or "nchar" or "ntext") && rawMaxLen != -1
                    ? rawMaxLen / 2
                    : rawMaxLen)
                : null;

            results.Add(new RoutineParameter(
                Name:       reader.GetString(0),
                DataType:   dataType,
                Direction:  reader.GetBoolean(2) ? ParameterDirection.InputOutput : ParameterDirection.Input,
                HasDefault: reader.GetBoolean(3),
                MaxLength:  maxLength,
                Precision:  reader.IsDBNull(5) ? null : reader.GetByte(5),
                Scale:      reader.IsDBNull(6) ? null : reader.GetByte(6),
                IsNullable: reader.IsDBNull(7) || reader.GetBoolean(7)));
        }
        return results;
    }

    // DbParameter.Precision/Scale are not on the base class — set via reflection.
    private static void SetPrecisionScale(DbParameter p, byte precision, byte scale)
    {
        var t = p.GetType();
        t.GetProperty("Precision")?.SetValue(p, precision);
        t.GetProperty("Scale")?.SetValue(p, scale);
    }

    private static string NormName(string name) => name.TrimStart('@').ToLowerInvariant();

    private static void AddParam(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
