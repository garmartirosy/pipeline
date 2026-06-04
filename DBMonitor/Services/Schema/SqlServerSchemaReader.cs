using System.Data;
using System.Data.Common;
using DBMonitor.Services.Import;

namespace DBMonitor.Services.Schema;

public class SqlServerSchemaReader : ISchemaReader
{
    private readonly DbConnection _connection;

    public SqlServerSchemaReader(DbConnection connection) => _connection = connection;

    public async Task<IReadOnlyList<SchemaObject>> ListObjectsAsync(CancellationToken ct = default)
    {
        var results = new List<SchemaObject>();

        // Tables and views
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest')
                  AND TABLE_SCHEMA NOT LIKE 'db[_]%'
                ORDER BY TABLE_SCHEMA, TABLE_NAME
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(2) == "VIEW" ? SchemaObjectType.View : SchemaObjectType.Table;
                results.Add(new SchemaObject(reader.GetString(0), reader.GetString(1), type));
            }
        }

        // Stored procedures and functions
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ROUTINE_SCHEMA, ROUTINE_NAME, ROUTINE_TYPE
                FROM INFORMATION_SCHEMA.ROUTINES
                WHERE ROUTINE_SCHEMA NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest')
                  AND ROUTINE_SCHEMA NOT LIKE 'db[_]%'
                ORDER BY ROUTINE_SCHEMA, ROUTINE_NAME
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(2) == "FUNCTION" ? SchemaObjectType.Function : SchemaObjectType.StoredProcedure;
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
                c.name                                                                  AS col_name,
                ty.name                                                                 AS data_type,
                c.is_nullable,
                CASE
                    WHEN ty.name IN ('nvarchar','nchar','ntext') AND c.max_length <> -1
                        THEN CAST(c.max_length / 2 AS int)
                    WHEN c.max_length = -1 THEN -1
                    ELSE CAST(c.max_length AS int)
                END                                                                     AS char_max_length,
                CAST(c.precision AS int)                                                AS num_precision,
                CAST(c.scale AS int)                                                    AS num_scale,
                CAST(CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END AS bit)      AS is_pk,
                c.is_identity,
                c.is_computed,
                CAST(CASE WHEN dc.definition IS NOT NULL THEN 1 ELSE 0 END AS bit)     AS has_default,
                dc.definition                                                           AS default_val,
                c.column_id
            FROM sys.columns c
            JOIN sys.types   ty ON ty.user_type_id   = c.user_type_id
            JOIN sys.objects o  ON o.object_id        = c.object_id
            JOIN sys.schemas s  ON s.schema_id        = o.schema_id
            LEFT JOIN sys.default_constraints dc
                ON  dc.parent_object_id = c.object_id
                AND dc.parent_column_id = c.column_id
            LEFT JOIN (
                SELECT ic.column_id, ic.object_id
                FROM sys.index_columns ic
                JOIN sys.indexes i ON i.object_id = ic.object_id
                                  AND i.index_id   = ic.index_id
                WHERE i.is_primary_key = 1
            ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            WHERE s.name = @schema
              AND o.name = @table
            ORDER BY c.column_id
            """;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@table", table);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ColumnInfo(
                Name:            reader.GetString(0),
                DataType:        reader.GetString(1),
                IsNullable:      reader.GetBoolean(2),
                MaxLength:       reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Precision:       reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Scale:           reader.IsDBNull(5) ? null : reader.GetInt32(5),
                IsPrimaryKey:    reader.GetBoolean(6),
                IsIdentity:      reader.GetBoolean(7),
                IsComputed:      reader.GetBoolean(8),
                HasDefault:      reader.GetBoolean(9),
                DefaultValue:    reader.IsDBNull(10) ? null : reader.GetString(10),
                OrdinalPosition: reader.GetInt32(11)));
        }
        return results;
    }

    public async Task<IReadOnlyList<ImportColumnInfo>> GetImportColumnsAsync(
        string schema, string table, CancellationToken ct = default)
    {
        var results = new List<ImportColumnInfo>();
        await using var cmd = _connection.CreateCommand();
        // max_length is bytes; nvarchar/nchar store 2 bytes per char; -1 = MAX
        cmd.CommandText = """
            SELECT
                c.name                                                          AS column_name,
                ty.name                                                         AS data_type,
                c.is_nullable,
                c.is_identity,
                c.is_computed,
                CASE WHEN dc.definition IS NOT NULL THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS has_default,
                CASE
                    WHEN ty.name IN ('nvarchar','nchar','ntext') AND c.max_length <> -1
                        THEN CAST(c.max_length / 2 AS int)
                    WHEN c.max_length = -1 THEN -1
                    ELSE CAST(c.max_length AS int)
                END                                                             AS char_max_length,
                c.column_id                                                     AS ordinal_position
            FROM sys.columns c
            JOIN sys.types ty      ON ty.user_type_id   = c.user_type_id
            JOIN sys.objects o     ON o.object_id        = c.object_id
            JOIN sys.schemas s     ON s.schema_id        = o.schema_id
            LEFT JOIN sys.default_constraints dc
                ON  dc.parent_object_id = c.object_id
                AND dc.parent_column_id = c.column_id
            WHERE s.name = @schema AND o.name = @table
            ORDER BY c.column_id
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
        string? definition = null;
        SchemaObjectType? routineType = null;
        var parameters = new List<RoutineParameter>();

        await using var cmd = _connection.CreateCommand();
        // Column layout:
        // 0  m.definition
        // 1  param_name      (p.name)
        // 2  data_type       (ty.name)
        // 3  p.is_output
        // 4  p.has_default_value
        // 5  p.max_length    (smallint; bytes — nvarchar/nchar require ÷2)
        // 6  p.precision     (tinyint)
        // 7  p.scale         (tinyint)
        // 8  p.is_nullable   (bit)
        // 9  default_text    (TRY_CONVERT of sql_variant; NULL for T-SQL expression defaults — known SQL Server limitation)
        // 10 o.type          (char(2): 'P'=proc, 'FN'=scalar, 'IF'=inline TVF, 'TF'=multi-stmt TVF)
        cmd.CommandText = """
            SELECT
                m.definition,
                p.name                                                    AS param_name,
                ty.name                                                   AS data_type,
                p.is_output,
                p.has_default_value,
                p.max_length,
                p.precision,
                p.scale,
                p.is_nullable,
                CASE WHEN p.has_default_value = 1
                     THEN TRY_CONVERT(nvarchar(256), p.default_value)
                END                                                       AS default_text,
                o.type                                                    AS object_type
            FROM sys.objects    o
            JOIN sys.schemas    s  ON s.schema_id  = o.schema_id
            JOIN sys.sql_modules m ON m.object_id  = o.object_id
            LEFT JOIN sys.parameters p  ON p.object_id    = o.object_id
                                       AND p.parameter_id > 0
            LEFT JOIN sys.types      ty ON ty.user_type_id = p.user_type_id
            WHERE s.name = @schema
              AND o.name = @name
            ORDER BY p.parameter_id
            """;
        AddParam(cmd, "@schema", schema);
        AddParam(cmd, "@name", name);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            definition ??= reader.IsDBNull(0)
                ? "[definition unavailable — object may be encrypted]"
                : reader.GetString(0);

            if (routineType is null && !reader.IsDBNull(10))
            {
                routineType = reader.GetString(10).Trim() switch
                {
                    "FN" or "IF" or "TF" or "AF" => SchemaObjectType.Function,
                    _ => SchemaObjectType.StoredProcedure,
                };
            }

            if (!reader.IsDBNull(1))
            {
                var dataType = reader.IsDBNull(2) ? "nvarchar" : reader.GetString(2);

                // max_length is bytes; nvarchar/nchar store 2 bytes per char; -1 = MAX
                var rawMaxLen = reader.IsDBNull(5) ? (int?)null : (int)reader.GetInt16(5);
                int? maxLength = rawMaxLen.HasValue
                    ? ((dataType is "nvarchar" or "nchar" or "ntext") && rawMaxLen != -1
                        ? rawMaxLen / 2
                        : rawMaxLen)
                    : null;

                parameters.Add(new RoutineParameter(
                    Name:            reader.GetString(1),
                    DataType:        dataType,
                    // SQL Server OUTPUT params map to InputOutput in ADO.NET — they can receive an initial value
                    Direction:       reader.GetBoolean(3) ? ParameterDirection.InputOutput : ParameterDirection.Input,
                    HasDefault:      reader.GetBoolean(4),
                    MaxLength:       maxLength,
                    Precision:       reader.IsDBNull(6) ? null : reader.GetByte(6),
                    Scale:           reader.IsDBNull(7) ? null : reader.GetByte(7),
                    IsNullable:      reader.IsDBNull(8) || reader.GetBoolean(8),
                    DefaultValueText: reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }

        return new RoutineInfo(
            schema,
            name,
            definition ?? "[object not found]",
            parameters,
            routineType ?? SchemaObjectType.StoredProcedure);
    }

    private static void AddParam(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
