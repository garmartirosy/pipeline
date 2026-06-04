namespace DBMonitor.Services.Query;

public record TableDataPage(
    IReadOnlyList<ColumnDescriptor> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    long TotalCount,
    int Page,
    int PageSize,
    long ElapsedMs);
