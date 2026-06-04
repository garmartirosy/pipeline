using DBMonitor.Models;

namespace DBMonitor.Services.Import;

public class BulkImporterFactory : IBulkImporterFactory
{
    public IBulkImporter Create(DbProviderKind provider) => provider switch
    {
        DbProviderKind.SqlServer  => new SqlServerBulkImporter(),
        DbProviderKind.PostgreSql => new PostgreSqlBulkImporter(),
        _ => throw new NotSupportedException($"No bulk importer for provider '{provider}'."),
    };
}
