namespace DBMonitor.Services.Import;

public record ImportColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsIdentity,
    bool IsComputed,
    bool HasDefault,
    int? MaxLength,
    int OrdinalPosition);
