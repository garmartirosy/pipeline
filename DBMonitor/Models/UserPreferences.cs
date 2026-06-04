using System.ComponentModel.DataAnnotations;

namespace DBMonitor.Models;

public class UserPreferences
{
    [Key]
    public string OwnerId { get; set; } = default!;

    [MaxLength(20)]
    public string Theme { get; set; } = "System"; // Light | Dark | System

    public int DefaultPageSize { get; set; } = 50;
    public int DefaultQueryTimeout { get; set; } = 30;
    public int DefaultMaxRows { get; set; } = 1000;
    public bool ConfirmDestructiveByDefault { get; set; } = false;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
