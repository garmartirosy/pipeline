namespace DBMonitor.Services.Import;

public record ImportResult(
    long RowsRead,
    long RowsInserted,
    long RowsRejected,
    long ElapsedMs,
    IReadOnlyList<RowError> Errors,   // capped at 100; further errors counted in RowsRejected
    bool TruncatedExistingData,
    bool RolledBack,
    string? Message = null);
