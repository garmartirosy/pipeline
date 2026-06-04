using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DBMonitor.Models;

public class QueryAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string OwnerId { get; set; } = default!;

    public Guid ProfileId { get; set; }

    public DateTime ExecutedUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(8000)]
    public string Sql { get; set; } = default!;

    public int? RecordsAffected { get; set; }

    public long ElapsedMs { get; set; }

    public bool Success { get; set; }

    public bool RolledBack { get; set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }
}
