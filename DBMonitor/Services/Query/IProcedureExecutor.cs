using System.Data.Common;

namespace DBMonitor.Services.Query;

public interface IProcedureExecutor
{
    Task<ProcedureExecution> ExecuteAsync(DbConnection conn, ProcedureRequest request, CancellationToken ct);
}
