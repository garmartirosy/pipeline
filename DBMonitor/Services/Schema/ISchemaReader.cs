using DBMonitor.Services.Import;

namespace DBMonitor.Services.Schema;

public interface ISchemaReader
{
    Task<IReadOnlyList<SchemaObject>> ListObjectsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string schema, string table, CancellationToken ct = default);
    Task<IReadOnlyList<ImportColumnInfo>> GetImportColumnsAsync(string schema, string table, CancellationToken ct = default);
    Task<RoutineInfo> GetRoutineAsync(string schema, string name, CancellationToken ct = default);
}
