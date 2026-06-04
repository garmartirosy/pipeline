using System.ComponentModel.DataAnnotations;

namespace DBMonitor.Models;

public class DbConnectionProfile
{
    public Guid Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public DbProviderKind Provider { get; set; }

    // Stored encrypted via IConnectionStringProtector — never plaintext on disk.
    [Required]
    public string EncryptedConnectionString { get; set; } = string.Empty;

    // FK to AspNetUsers.Id
    [Required]
    public string OwnerId { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public bool IsPinned { get; set; }
    public int SortOrder { get; set; }

    // Visible and fully editable by all users; OwnerId is "system" for default connections.
    public bool IsShared { get; set; }
}
