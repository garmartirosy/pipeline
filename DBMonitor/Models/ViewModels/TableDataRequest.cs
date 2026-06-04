namespace DBMonitor.Models.ViewModels;

public class TableDataRequest
{
    public string Schema { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? OrderBy { get; set; }
    public bool Descending { get; set; }
    public List<ColumnFilterDto> Filters { get; set; } = new();
}

public class ColumnFilterDto
{
    public string Column { get; set; } = string.Empty;
    public string Op { get; set; } = "Equals";
    public string? Value { get; set; }
}
