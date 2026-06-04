using DBMonitor.Models;

namespace DBMonitor.Services.Import;

public interface IBulkImporterFactory
{
    IBulkImporter Create(DbProviderKind provider);
}
