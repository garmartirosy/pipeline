namespace DBMonitor.Services.Query;

public interface ITableDataReader
{
    Task<TableDataPage> ReadPageAsync(string schema, string table, TableQuery query, CancellationToken ct = default);
}
