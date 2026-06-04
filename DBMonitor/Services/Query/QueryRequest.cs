namespace DBMonitor.Services.Query;

public record QueryRequest(
    Guid ProfileId,
    string Sql,
    int? TimeoutSeconds,
    int? MaxRows,
    bool AllowDestructive);
