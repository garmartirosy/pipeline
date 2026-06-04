using System.Data;

namespace DBMonitor.Services.Schema;

public record RoutineParameter(
    string Name,
    string DataType,
    ParameterDirection Direction,
    bool HasDefault,
    int? MaxLength = null,
    byte? Precision = null,
    byte? Scale = null,
    bool IsNullable = true,
    string? DefaultValueText = null);
