using DBMonitor.Models;
using Microsoft.Data.SqlClient;
using Npgsql;
using CommonDbProviderFactory = System.Data.Common.DbProviderFactory;

namespace DBMonitor.Services;

public class DbProviderFactoryResolver : IDbProviderFactory
{
    public CommonDbProviderFactory GetFactory(DbProviderKind provider) => provider switch
    {
        DbProviderKind.SqlServer => SqlClientFactory.Instance,
        DbProviderKind.PostgreSql => NpgsqlFactory.Instance,
        _ => throw new NotSupportedException($"Provider '{provider}' is not supported."),
    };
}
