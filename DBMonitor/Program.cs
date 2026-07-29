using DBMonitor.Data;
using DBMonitor.Models;
using DBMonitor.Services;
using DBMonitor.Services.Import;
using DBMonitor.Services.Query;
using DBMonitor.Services.Schema;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

// Bootstrap configuration used to obtain the GitHub configuration token
// from environment variables, Azure App Settings, or User Secrets.
var bootstrap = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>(optional: true)
    .Build();

var githubToken = bootstrap["GITHUB_CONFIG_TOKEN"];

if (!string.IsNullOrWhiteSpace(githubToken))
{
    using var http = new HttpClient();

    http.DefaultRequestHeaders.UserAgent.ParseAdd("DBMonitor/1.0");
    http.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            githubToken);

    var envContent = await http.GetStringAsync(
        "https://raw.githubusercontent.com/garmartirosy/netdev/main/.env");

    DotNetEnv.Env.LoadContents(envContent);
}
else
{
    DotNetEnv.Env.TraversePath().Load();
}

var builder = WebApplication.CreateBuilder(args);

// ── Data ──────────────────────────────────────────────────────────────────────

var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "Connection string 'DefaultConnection' was not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ── Identity and Google authentication ────────────────────────────────────────

builder.Services
    .AddDefaultIdentity<IdentityUser>(options =>
    {
        // Local users can currently sign in without confirming their email.
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services
    .AddAuthentication()
    .AddGoogle(options =>
    {
        options.ClientId =
            builder.Configuration["Authentication:Google:ClientId"]
            ?? throw new InvalidOperationException(
                "Google Client ID is missing.");

        options.ClientSecret =
            builder.Configuration["Authentication:Google:ClientSecret"]
            ?? throw new InvalidOperationException(
                "Google Client Secret is missing.");
    });

// ── MVC, Razor Pages and HTTP services ────────────────────────────────────────

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddHttpClient();

// ── Data Protection ───────────────────────────────────────────────────────────

// TODO:
// Production deployments need persistent key storage such as Azure Key Vault,
// Redis, or an Azure file share. Otherwise, regenerated keys could make saved
// encrypted connection profiles unreadable after a container restart.
builder.Services.AddDataProtection();

// ── Domain services ───────────────────────────────────────────────────────────

builder.Services.AddScoped<
    IConnectionStringProtector,
    DataProtectionConnectionStringProtector>();

builder.Services.AddScoped<
    IDbProviderFactory,
    DbProviderFactoryResolver>();

builder.Services.AddScoped<
    IConnectionTester,
    ConnectionTester>();

builder.Services.AddSingleton<SchemaReaderFactory>();
builder.Services.AddSingleton<TableDataReaderFactory>();
builder.Services.AddSingleton<ProcedureExecutorFactory>();

builder.Services.AddSingleton<
    IBulkImporterFactory,
    BulkImporterFactory>();

builder.Services.AddSingleton<
    ICsvInspector,
    CsvInspector>();

builder.Services.AddSingleton<CsvSchemaInferrer>();

builder.Services.AddScoped<
    IQueryExecutor,
    QueryExecutor>();

builder.Services.AddHostedService<ImportSessionCleanupService>();

// ── Health checks ─────────────────────────────────────────────────────────────

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>();

// ── Upload limits ─────────────────────────────────────────────────────────────

builder.Services.Configure<
    Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 100_000_000;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100_000_000;
});

// All services must be registered before this line.
var app = builder.Build();

// ── Seed default connections ──────────────────────────────────────────────────

await SeedDefaultConnectionsAsync(app);

// ── HTTP request pipeline ─────────────────────────────────────────────────────

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

// Authentication must come before authorization.
app.UseAuthentication();
app.UseAuthorization();

// ── Routes ────────────────────────────────────────────────────────────────────

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();

// ── Operational endpoints ─────────────────────────────────────────────────────

app.MapHealthChecks("/health")
    .AllowAnonymous();

app.MapGet("/version", () =>
{
    return Results.Ok(new
    {
        version =
            typeof(Program).Assembly.GetName().Version?.ToString()
            ?? "0.0.0",

        utc = DateTime.UtcNow
    });
})
.AllowAnonymous();

app.Run();

// ── Default connection seeder ─────────────────────────────────────────────────

static async Task SeedDefaultConnectionsAsync(WebApplication app)
{
    var defaults = new[]
    {
        new
        {
            Id = new Guid(
                "00000000-0000-0000-0000-000000000001"),

            Name = "IndustryDB (Azure PostgreSQL)",

            Provider = DbProviderKind.PostgreSql,

            PlaintextConnStr =
                $"Host={app.Configuration["POSTGRES_HOST"]};" +
                $"Database={app.Configuration["POSTGRES_DB"]};" +
                $"Username={app.Configuration["POSTGRES_USER"]};" +
                $"Password={app.Configuration["POSTGRES_PASSWORD"]};" +
                $"Port={app.Configuration["POSTGRES_PORT"] ?? "5432"};" +
                "SSL Mode=Require;" +
                "Trust Server Certificate=true"
        }
    };

    await using var scope = app.Services.CreateAsyncScope();

    var db =
        scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

    var protector =
        scope.ServiceProvider
            .GetRequiredService<IConnectionStringProtector>();

    await db.Database.MigrateAsync();

    foreach (var definition in defaults)
    {
        var existing =
            await db.ConnectionProfiles.FindAsync(definition.Id);

        var encryptedConnectionString =
            protector.Protect(definition.PlaintextConnStr);

        if (existing is null)
        {
            db.ConnectionProfiles.Add(
                new DbConnectionProfile
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    Provider = definition.Provider,

                    EncryptedConnectionString =
                        encryptedConnectionString,

                    OwnerId = "system",
                    IsShared = true,
                    CreatedUtc = DateTime.UtcNow
                });
        }
        else
        {
            existing.EncryptedConnectionString =
                encryptedConnectionString;

            existing.Name = definition.Name;
        }
    }

    await db.SaveChangesAsync();
}