namespace DBMonitor.Services.Import;

public record ColumnMapping(
    string CsvHeader,
    int CsvIndex,
    string TargetColumn,
    string TargetDataType,
    bool TargetIsNullable,
    bool Skip);
