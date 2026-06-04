using DBMonitor.Data;
using Microsoft.EntityFrameworkCore;

namespace DBMonitor.Services;

public class ImportSessionCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImportSessionCleanupService> _logger;

    public ImportSessionCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<ImportSessionCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            await SweepExpiredAsync(stoppingToken);
        }
    }

    private async Task SweepExpiredAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var expired = await db.ImportSessions
                .Where(s => s.ExpiresUtc < DateTime.UtcNow)
                .ToListAsync(ct);

            foreach (var session in expired)
            {
                try
                {
                    if (File.Exists(session.TempFilePath))
                        File.Delete(session.TempFilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Could not delete expired temp file {Path}", session.TempFilePath);
                }
            }

            if (expired.Count > 0)
            {
                db.ImportSessions.RemoveRange(expired);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "ImportSessionCleanup: removed {Count} expired session(s).", expired.Count);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ImportSessionCleanup sweep failed.");
        }
    }
}
