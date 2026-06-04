using DBMonitor.Models;
using System.Data.Common;

namespace DBMonitor.Services.Query;

public class TableDataReaderFactory
{
    public ITableDataReader Create(DbProviderKind provider, DbConnection openConnection) => provider switch
    {
        DbProviderKind.SqlServer  => new SqlServerTableDataReader(openConnection),
        DbProviderKind.PostgreSql => new PostgreSqlTableDataReader(openConnection),
        _ => throw new NotSupportedException($"Provider '{provider}' is not supported."),
    };
}
