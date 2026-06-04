using DBMonitor.Models;

namespace DBMonitor.Services;

public record ConnectionTestResult(bool Success, long ElapsedMs, string? ErrorMessage);

public interface IConnectionTester
{
    Task<ConnectionTestResult> TestAsync(DbProviderKind provider, string connectionString, CancellationToken cancellationToken = default);
}
