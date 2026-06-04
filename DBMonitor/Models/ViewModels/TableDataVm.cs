using DBMonitor.Models;

namespace DBMonitor.Models.ViewModels;

public class TableDataVm
{
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public DbProviderKind Provider { get; set; }
    public string Schema { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
}
