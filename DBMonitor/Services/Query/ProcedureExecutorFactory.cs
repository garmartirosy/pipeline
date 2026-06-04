using DBMonitor.Models;

namespace DBMonitor.Services.Query;

public class ProcedureExecutorFactory
{
    public IProcedureExecutor Create(DbProviderKind provider) => provider switch
    {
        DbProviderKind.SqlServer  => new SqlServerProcedureExecutor(),
        DbProviderKind.PostgreSql => new PostgreSqlProcedureExecutor(),
        _ => throw new NotSupportedException($"Provider '{provider}' is not supported for procedure execution."),
    };
}
