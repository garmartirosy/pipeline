namespace DBMonitor.Services.Schema;

public record RoutineInfo(
    string Schema,
    string Name,
    string Definition,
    IReadOnlyList<RoutineParameter> Parameters,
    SchemaObjectType Type = SchemaObjectType.StoredProcedure);
