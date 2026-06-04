namespace DBMonitor.Services.Query;

public record ProcedureRequest(
    Guid ProfileId,
    string Schema,
    string Name,
    IReadOnlyList<ProcedureParameterValue> Parameters,
    int? TimeoutSeconds,
    int? MaxRows);
