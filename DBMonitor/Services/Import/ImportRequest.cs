namespace DBMonitor.Services.Import;

public record ImportRequest(
    Guid ProfileId,
    string Schema,
    string Table,
    IReadOnlyList<ColumnMapping> Mappings,
    char Delimiter,
    bool HasHeader,
    string EncodingName,
    string CultureName,
    NullHandling NullHandling,
    ExistingDataMode ExistingDataMode,
    int BatchSize,                   // clamped [100, 100_000]; ignored by PostgreSQL COPY
    string TempFilePath,
    string OriginalFileName,
    bool AbortOnAnyError = false);   // roll back if RowsRejected > 0
