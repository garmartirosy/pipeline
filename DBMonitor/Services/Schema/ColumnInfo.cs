namespace DBMonitor.Services.Schema;

public record ColumnInfo(
    string  Name,
    string  DataType,
    bool    IsNullable,
    int?    MaxLength,
    int?    Precision,
    int?    Scale,
    bool    IsPrimaryKey,
    bool    IsIdentity,
    bool    IsComputed,
    bool    HasDefault,
    string? DefaultValue,
    int     OrdinalPosition);
