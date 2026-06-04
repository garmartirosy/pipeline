using System.Data.Common;

namespace DBMonitor.Services.Import;

public interface IBulkImporter
{
    Task<ImportResult> ImportAsync(DbConnection conn, ImportRequest request, CancellationToken ct);
}
