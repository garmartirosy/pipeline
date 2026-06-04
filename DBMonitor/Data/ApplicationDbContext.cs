using DBMonitor.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DBMonitor.Data;

public class ApplicationDbContext : IdentityDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<DbConnectionProfile> ConnectionProfiles => Set<DbConnectionProfile>();
    public DbSet<QueryAuditEntry>     QueryAuditEntries  => Set<QueryAuditEntry>();
    public DbSet<ImportSession>       ImportSessions     => Set<ImportSession>();
    public DbSet<SavedQuery>          SavedQueries       => Set<SavedQuery>();
    public DbSet<UserPreferences>     UserPreferences    => Set<UserPreferences>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<DbConnectionProfile>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).IsRequired().HasMaxLength(100);
            e.Property(p => p.EncryptedConnectionString).IsRequired();
            e.Property(p => p.OwnerId).IsRequired();
            e.HasIndex(p => p.OwnerId);
        });

        builder.Entity<QueryAuditEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.OwnerId).IsRequired();
            e.Property(a => a.Sql).IsRequired().HasMaxLength(8000);
            e.Property(a => a.ErrorMessage).HasMaxLength(2000);
            e.HasIndex(a => new { a.OwnerId, a.ExecutedUtc });
        });

        builder.Entity<ImportSession>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.OwnerId).IsRequired();
            e.Property(s => s.TempFilePath).IsRequired();
            e.HasIndex(s => new { s.OwnerId, s.ExpiresUtc });
        });

        builder.Entity<SavedQuery>(e =>
        {
            e.HasKey(q => q.Id);
            e.Property(q => q.OwnerId).IsRequired();
            e.Property(q => q.Name).IsRequired().HasMaxLength(100);
            e.Property(q => q.Sql).IsRequired().HasMaxLength(50_000);
            e.Property(q => q.Description).HasMaxLength(500);
            e.HasIndex(q => new { q.OwnerId, q.ProfileId });
        });

        builder.Entity<UserPreferences>(e =>
        {
            e.HasKey(p => p.OwnerId);
            e.Property(p => p.Theme).HasMaxLength(20);
        });
    }
}
