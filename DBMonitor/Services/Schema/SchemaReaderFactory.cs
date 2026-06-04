using DBMonitor.Models;
using System.Data.Common;

namespace DBMonitor.Services.Schema;

public class SchemaReaderFactory
{
    public ISchemaReader Create(DbProviderKind provider, DbConnection openConnection) => provider switch
    {
        DbProviderKind.SqlServer  => new SqlServerSchemaReader(openConnection),
        DbProviderKind.PostgreSql => new PostgreSqlSchemaReader(openConnection),
        _ => throw new NotSupportedException($"Provider '{provider}' is not supported."),
    };
}
