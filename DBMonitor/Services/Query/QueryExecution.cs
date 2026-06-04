namespace DBMonitor.Services.Query;

public record QueryExecution(
    IReadOnlyList<QueryResultSet> ResultSets,
    int RecordsAffected,
    long ElapsedMs,
    bool RolledBack,
    string? Message);
