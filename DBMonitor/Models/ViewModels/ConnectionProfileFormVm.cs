using System.ComponentModel.DataAnnotations;

namespace DBMonitor.Models.ViewModels;

public class ConnectionProfileFormVm
{
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    public DbProviderKind Provider { get; set; }

    // Plaintext, only in-memory during POST. Required on Create;
    // on Edit, leave blank to keep the existing connection string.
    [StringLength(4000)]
    public string? ConnectionString { get; set; }

    // Edit only: pre-set to true so the form can communicate intent clearly.
    public bool KeepExistingConnectionString { get; set; }
}
