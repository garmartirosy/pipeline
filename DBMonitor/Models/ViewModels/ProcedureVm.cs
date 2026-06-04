using DBMonitor.Services.Schema;

namespace DBMonitor.Models.ViewModels;

public class ProcedureVm : BrowseVm
{
    public string Schema { get; set; } = default!;
    public string Name { get; set; } = default!;
    public RoutineInfo Routine { get; set; } = default!;
    public string ExecEditorUrl { get; set; } = default!;
}
