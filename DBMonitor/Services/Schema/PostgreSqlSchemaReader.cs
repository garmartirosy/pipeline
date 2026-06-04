using System.Data;
using System.Data.Common;
using DBMonitor.Services.Import;

namespace DBMonitor.Services.Schema;

public class PostgreSqlSchemaReader : ISchemaReader
{
    private readonly DbConnection _connection;

    public PostgreSqlSchemaReader(DbConnection connection) => _connection = connection;

    public async Task<IReadOnlyList<SchemaObject>> ListObjectsAsync(CancellationToken ct = default)
    {
        var results = new List<SchemaObject>();

        // Tables and views
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT table_schema, table_name, table_type
                FROM information_schema.tables
                WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
                ORDER BY table_schema, table_name
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(2) == "VIEW" ? SchemaObjectType.View : SchemaObjectType.Table;
                results.Add(new SchemaObject(reader.GetString(0), reader.GetString(1), type));
            }
        }

        // Functions and procedures
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT routine_schema, routine_name, routine_type
                FROM information_schema.routines
                WHERE routine_schema NOT IN ('pg_catalog', 'information_schema')
                ORDER BY routine_schema, routine_name
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(2) == "PROCEDURE" ? SchemaObjectType.StoredProcedure : SchemaObjectType.Function;
                results.Add(new SchemaObject(reader.GetString(0), reader.GetString(1), type));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string schema, string table, CancellationToken ct = default)
    {
        var results = new List<ColumnInfo>();
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                c.column_name,
                c.data_type,
                c.is_nullable,
                c.character_maximum_length,
                c.numeric_precision,
                c.numeric_scale,
                c.ordinal_position,
                (pk.column_name IS NOT NULL)                                                    AS is_pk,
                (c.is_identity = 'YES')                                                         AS is_identity,
                (c.column_default IS NOT NULL AND c.is_identity = 'NO')                        AS has_default,
                c.column_default
            FROM information_schema.columns c
            LEFT JOIN (
                SELECT kcu.column_name
                FROM information_schema.key_column_usage kcu
                JOIN information_schema.table_constraints tc
                    ON  tc.constraint_name   = kcu.constraint_name
                    AND tc.constraint_schema = kcu.constraint_schema
                    AND tc.table_name        = kcu.table_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
                  AND kcu.table_schema   = @schema
                  AND kcu.table_name     = @table
            ) pk ON pk.column_name = c.column_name
            WHERE c.table_schema = @schema
              AND c.table_name   = @table
            ORDER BY c.ordinal_position
            """;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ColumnInfo(
                Name:            reader.GetString(0),
                DataType:        reader.GetString(1),
                IsNullable:      reader.GetString(2) == "YES",
                MaxLength:       reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Precision:       reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Scale:           reader.IsDBNull(5) ? null : reader.GetInt32(5),
                OrdinalPosition: reader.GetInt32(6),
                IsPrimaryKey:    reader.GetBoolean(7),
                IsIdentity:      reader.GetBoolean(8),
                IsComputed:      false,
                HasDefault:      reader.GetBoolean(9),
                DefaultValue:    reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return results;
    }

    public async Task<IReadOnlyList<ImportColumnInfo>> GetImportColumnsAsync(
        string schema, string table, CancellationToken ct = default)
    {
        var results = new List<ImportColumnInfo>();
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                c.column_name,
                c.data_type,
                c.is_nullable = 'YES'       AS is_nullable,
                c.is_identity = 'YES'       AS is_identity,
                c.is_generated = 'ALWAYS'   AS is_computed,
                c.column_default IS NOT NULL AS has_default,
                c.character_maximum_length,
                c.ordinal_position
            FROM information_schema.columns c
            WHERE c.table_schema = @schema
              AND c.table_name   = @table
            ORDER BY c.ordinal_position
            """;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ImportColumnInfo(
                Name:            reader.GetString(0),
                DataType:        reader.GetString(1),
                IsNullable:      reader.GetBoolean(2),
                IsIdentity:      reader.GetBoolean(3),
                IsComputed:      reader.GetBoolean(4),
                HasDefault:      reader.GetBoolean(5),
                MaxLength:       reader.IsDBNull(6) ? null : reader.GetInt32(6),
                OrdinalPosition: reader.GetInt32(7)));
        }
        return results;
    }

    public async Task<RoutineInfo> GetRoutineAsync(string schema, string name, CancellationToken ct = default)
    {
        // Definition from pg_proc; routine_type from information_schema.routines
        string definition;
        var routineType = SchemaObjectType.StoredProcedure;
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT pg_get_functiondef(p.oid), r.routine_type
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                JOIN information_schema.routines r
                    ON r.routine_schema = n.nspname AND r.routine_name = p.proname
                WHERE n.nspname = @schema AND p.proname = @name
                LIMIT 1
                """;
            AddParam(cmd, "@schema", schema);
            AddParam(cmd, "@name", name);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                definition = r.IsDBNull(0) ? "[definition unavailable]" : r.GetString(0);
                routineType = (!r.IsDBNull(1) && r.GetString(1) == "PROCEDURE")
                    ? SchemaObjectType.StoredProcedure
                    : SchemaObjectType.Function;
            }
            else
            {
                definition = "[object not found]";
            }
        }

        // Parameters: information_schema includes character_maximum_length, numeric_precision/scale, parameter_default
        // PostgreSQL parameters do not have a separate is_nullable — all params accept NULL.
        var parameters = new List<RoutineParameter>();
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    par.parameter_name,
                    par.data_type,
                    par.parameter_mode,
                    par.parameter_default IS NOT NULL      AS has_default,
                    par.character_maximum_length,
                    par.numeric_precision,
                    par.numeric_scale,
                    par.parameter_default
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
            AddParam(cmd, "@name", name);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                parameters.Add(new RoutineParameter(
                    Name:             reader.IsDBNull(0) ? "" : reader.GetString(0),
                    DataType:         reader.GetString(1),
                    Direction:        ParseDirection(reader.IsDBNull(2) ? "IN" : reader.GetString(2)),
                    HasDefault:       reader.GetBoolean(3),
                    MaxLength:        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Precision:        reader.IsDBNull(5) ? null : (byte?)reader.GetInt32(5),
                    Scale:            reader.IsDBNull(6) ? null : (byte?)reader.GetInt32(6),
                    IsNullable:       true, // PostgreSQL parameters have no separate nullable flag; all accept NULL
                    DefaultValueText: reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        return new RoutineInfo(schema, name, definition, parameters, routineType);
    }

    private static ParameterDirection ParseDirection(string mode) => mode.ToUpperInvariant() switch
    {
        "OUT"   => ParameterDirection.Output,
        "INOUT" => ParameterDirection.InputOutput,
        _       => ParameterDirection.Input,
    };

    private static void AddParam(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
