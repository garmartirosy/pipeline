namespace DBMonitor.Services.Query;

public record TableQuery(
    int Page,
    int PageSize,
    string? OrderByColumn,
    bool Descending,
    IReadOnlyList<ColumnFilter> Filters);
