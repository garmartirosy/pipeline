namespace DBMonitor.Services.Query;

public record QueryResultSet(
    IReadOnlyList<ColumnDescriptor> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated);
