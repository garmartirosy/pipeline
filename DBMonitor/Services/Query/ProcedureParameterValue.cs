namespace DBMonitor.Services.Query;

public record ProcedureParameterValue(
    string Name,
    string? RawValue,
    bool IsNull,
    bool UseDefault = false);
