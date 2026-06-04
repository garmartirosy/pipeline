namespace DBMonitor.Services.Import;

public record RowError(long LineNumber, string CsvLine, string Error);
