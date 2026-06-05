using DBMonitor.Data;
using DBMonitor.Models;
using DBMonitor.Services;
using DBMonitor.Services.Import;
using DBMonitor.Services.Query;
using DBMonitor.Services.Schema;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

// Load .env for local development. On Azure, App Settings supply the same vars.
DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

// ── Data ──────────────────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ── Identity ──────────────────────────────────────────────────────────────────
builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = false)
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();

// ── Data Protection ───────────────────────────────────────────────────────────
// TODO: Production deployments need persistent key storage (Azure Key Vault, Redis, file share, etc.)
// or Data Protection keys regenerate on container restart, making every saved profile undecryptable.
builder.Services.AddDataProtection();

// ── Domain services ───────────────────────────────────────────────────────────
builder.Services.AddScoped<IConnectionStringProtector, DataProtectionConnectionStringProtector>();
builder.Services.AddScoped<IDbProviderFactory, DbProviderFactoryResolver>();
builder.Services.AddScoped<IConnectionTester, ConnectionTester>();
builder.Services.AddSingleton<SchemaReaderFactory>();
builder.Services.AddSingleton<TableDataReaderFactory>();
builder.Services.AddSingleton<ProcedureExecutorFactory>();
builder.Services.AddSingleton<IBulkImporterFactory, BulkImporterFactory>();
builder.Services.AddSingleton<ICsvInspector, CsvInspector>();
builder.Services.AddSingleton<CsvSchemaInferrer>();
builder.Services.AddScoped<IQueryExecutor, QueryExecutor>();
builder.Services.AddHostedService<ImportSessionCleanupService>();

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>();

// ── Upload limits ─────────────────────────────────────────────────────────────
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 100_000_000;
});
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 100_000_000;
});

var app = builder.Build();

// ── Seed default connections ──────────────────────────────────────────────────
await SeedDefaultConnectionsAsync(app);

// ── Pipeline ──────────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error/500");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/Error/{0}");

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();

// ── Operational endpoints ─────────────────────────────────────────────────────
app.MapHealthChecks("/health").AllowAnonymous();
app.MapGet("/version", () => Results.Ok(new {
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
    utc     = DateTime.UtcNow,
})).AllowAnonymous();

app.Run();

// ── Default connection seeder ─────────────────────────────────────────────────
static async Task SeedDefaultConnectionsAsync(WebApplication app)
{
    var defaults = new[]
    {
        new
        {
            Id               = new Guid("00000000-0000-0000-0000-000000000001"),
            Name             = "IndustryDB (Azure PostgreSQL)",
            Provider         = DbProviderKind.PostgreSql,
            PlaintextConnStr = $"Host={app.Configuration["POSTGRES_HOST"]};" +
                               $"Database={app.Configuration["POSTGRES_DB"]};" +
                               $"Username={app.Configuration["POSTGRES_USER"]};" +
                               $"Password={app.Configuration["POSTGRES_PASSWORD"]};" +
                               $"Port={app.Configuration["POSTGRES_PORT"] ?? "5432"};" +
                               "SSL Mode=Require;Trust Server Certificate=true",
        },
    };

    await using var scope = app.Services.CreateAsyncScope();
    var db        = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var protector = scope.ServiceProvider.GetRequiredService<IConnectionStringProtector>();

    await db.Database.MigrateAsync();

    foreach (var def in defaults)
    {
        var existing  = await db.ConnectionProfiles.FindAsync(def.Id);
        var encrypted = protector.Protect(def.PlaintextConnStr);
        if (existing is null)
        {
            db.ConnectionProfiles.Add(new DbConnectionProfile
            {
                Id                        = def.Id,
                Name                      = def.Name,
                Provider                  = def.Provider,
                EncryptedConnectionString = encrypted,
                OwnerId                   = "system",
                IsShared                  = true,
                CreatedUtc                = DateTime.UtcNow,
            });
        }
        else
        {
            existing.EncryptedConnectionString = encrypted;
            existing.Name                      = def.Name;
        }
    }

    await db.SaveChangesAsync();
}
////
/// 
/// 