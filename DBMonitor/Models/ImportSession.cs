using System.ComponentModel.DataAnnotations;

namespace DBMonitor.Models;

public class ImportSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string OwnerId { get; set; } = default!;

    public Guid ProfileId { get; set; }

    [MaxLength(200)]
    public string Schema { get; set; } = "";

    [MaxLength(200)]
    public string Table { get; set; } = "";

    [MaxLength(500)]
    public string OriginalFileName { get; set; } = "";

    [Required]
    public string TempFilePath { get; set; } = default!;

    public long FileSizeBytes { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow.AddHours(1);
}
