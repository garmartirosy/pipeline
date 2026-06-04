using System.ComponentModel.DataAnnotations;

namespace DBMonitor.Models;

public class SavedQuery
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string OwnerId { get; set; } = default!;

    // Null = global snippet; set = scoped to one profile.
    public Guid? ProfileId { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = default!;

    [MaxLength(500)]
    public string? Description { get; set; }

    [Required, MaxLength(50_000)]
    public string Sql { get; set; } = default!;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public int UseCount { get; set; }
    public DateTime? LastUsedUtc { get; set; }
}
