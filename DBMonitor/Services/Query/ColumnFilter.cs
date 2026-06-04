namespace DBMonitor.Services.Query;

public record ColumnFilter(string Column, FilterOp Op, string? Value);
