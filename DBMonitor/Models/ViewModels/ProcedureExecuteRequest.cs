namespace DBMonitor.Models.ViewModels;

public class ProcedureExecuteRequest
{
    public string Schema { get; set; } = default!;
    public string Name { get; set; } = default!;
    public List<ProcedureParameterValueDto> Parameters { get; set; } = new();
    public int? TimeoutSeconds { get; set; }
    public int? MaxRows { get; set; }
}

public class ProcedureParameterValueDto
{
    public string Name { get; set; } = default!;
    public string? RawValue { get; set; }
    public bool IsNull { get; set; }
    public bool UseDefault { get; set; }
}
