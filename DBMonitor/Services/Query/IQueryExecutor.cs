using System.Data.Common;

namespace DBMonitor.Services.Query;

public interface IQueryExecutor
{
    Task<QueryExecution> ExecuteAsync(DbConnection conn, QueryRequest request, CancellationToken ct);
}
