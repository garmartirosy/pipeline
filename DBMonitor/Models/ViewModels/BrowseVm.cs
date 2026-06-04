using DBMonitor.Models;

namespace DBMonitor.Models.ViewModels;

public class BrowseVm
{
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public DbProviderKind Provider { get; set; }
}
