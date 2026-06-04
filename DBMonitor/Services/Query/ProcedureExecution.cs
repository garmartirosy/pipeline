namespace DBMonitor.Services.Query;

public record ProcedureExecution(
    IReadOnlyList<QueryResultSet> ResultSets,
    int? ReturnValue,
    IReadOnlyDictionary<string, object?> OutputValues,
    int RecordsAffected,
    long ElapsedMs,
    string? Message);
