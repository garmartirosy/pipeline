using DBMonitor.Data;
using DBMonitor.Models;
using DBMonitor.Models.ViewModels;
using DBMonitor.Services;
using DBMonitor.Services.Query;
using DBMonitor.Services.Schema;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace DBMonitor.Controllers;

[Authorize]
public class SchemaController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IConnectionStringProtector _protector;
    private readonly IDbProviderFactory _providerFactory;
    private readonly SchemaReaderFactory _schemaReaderFactory;
    private readonly TableDataReaderFactory _tableDataReaderFactory;
    private readonly UserManager<IdentityUser> _userManager;

    public SchemaController(
        ApplicationDbContext db,
        IConnectionStringProtector protector,
        IDbProviderFactory providerFactory,
        SchemaReaderFactory schemaReaderFactory,
        TableDataReaderFactory tableDataReaderFactory,
        UserManager<IdentityUser> userManager)
    {
        _db = db;
        _protector = protector;
        _providerFactory = providerFactory;
        _schemaReaderFactory = schemaReaderFactory;
        _tableDataReaderFactory = tableDataReaderFactory;
        _userManager = userManager;
    }

    // ── Browse shell ─────────────────────────────────────────────────────────
    // No DB connection opened here — JS fetches Objects on load.

    [HttpGet]
    [Route("Schema/Browse/{profileId:guid}")]
    public async Task<IActionResult> Browse(Guid profileId, CancellationToken ct)
    {
        var profile = await FindProfileAsync(profileId, ct);
        if (profile is null) return NotFound();

        return View(new BrowseVm
        {
            ProfileId = profileId,
            ProfileName = profile.Name,
            Provider = profile.Provider,
        });
    }

    // ── Objects list (JSON) ───────────────────────────────────────────────────

    [HttpGet]
    [Route("Schema/Objects/{profileId:guid}")]
    public async Task<IActionResult> Objects(Guid profileId, CancellationToken ct)
    {
        try
        {
            var opened = await OpenForCurrentUserAsync(profileId, ct);
            if (opened is null) return NotFound();
            var (profile, conn) = opened.Value;

            await using (conn)
            {
                var reader = _schemaReaderFactory.Create(profile.Provider, conn);
                var objects = await reader.ListObjectsAsync(ct);
                return Json(new
                {
                    tables     = objects.Where(o => o.Type == SchemaObjectType.Table)
                                        .Select(o => new { o.Schema, o.Name }),
                    views      = objects.Where(o => o.Type == SchemaObjectType.View)
                                        .Select(o => new { o.Schema, o.Name }),
                    procedures = objects.Where(o => o.Type == SchemaObjectType.StoredProcedure)
                                        .Select(o => new { o.Schema, o.Name }),
                    functions  = objects.Where(o => o.Type == SchemaObjectType.Function)
                                        .Select(o => new { o.Schema, o.Name }),
                });
            }
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // ── Columns for one table/view (JSON) ─────────────────────────────────────

    [HttpGet]
    [Route("Schema/Columns/{profileId:guid}")]
    public async Task<IActionResult> Columns(
        Guid profileId,
        [FromQuery] string schema,
        [FromQuery] string table,
        CancellationToken ct)
    {
        try
        {
            var opened = await OpenForCurrentUserAsync(profileId, ct);
            if (opened is null) return NotFound();
            var (profile, conn) = opened.Value;

            await using (conn)
            {
                var reader = _schemaReaderFactory.Create(profile.Provider, conn);
                var columns = await reader.GetColumnsAsync(schema, table, ct);
                return Json(columns);
            }
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // ── Routine definition + parameters (JSON) ────────────────────────────────

    [HttpGet]
    [Route("Schema/Routine/{profileId:guid}")]
    public async Task<IActionResult> Routine(
        Guid profileId,
        [FromQuery] string schema,
        [FromQuery] string name,
        CancellationToken ct)
    {
        try
        {
            var opened = await OpenForCurrentUserAsync(profileId, ct);
            if (opened is null) return NotFound();
            var (profile, conn) = opened.Value;

            await using (conn)
            {
                var reader = _schemaReaderFactory.Create(profile.Provider, conn);
                var routine = await reader.GetRoutineAsync(schema, name, ct);
                return Json(new
                {
                    routine.Schema,
                    routine.Name,
                    routine.Definition,
                    Type = routine.Type.ToString(),
                    Parameters = routine.Parameters.Select(p => new
                    {
                        p.Name,
                        p.DataType,
                        p.MaxLength,
                        p.Precision,
                        p.Scale,
                        Direction = p.Direction.ToString(),
                        p.HasDefault,
                        p.IsNullable,
                        p.DefaultValueText,
                    }),
                });
            }
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // ── Table data shell view ─────────────────────────────────────────────────

    [HttpGet]
    [Route("Schema/TableData/{profileId:guid}")]
    public async Task<IActionResult> TableData(
        Guid profileId,
        [FromQuery] string schema,
        [FromQuery] string table,
        CancellationToken ct)
    {
        var profile = await FindProfileAsync(profileId, ct);
        if (profile is null) return NotFound();

        return View(new TableDataVm
        {
            ProfileId   = profileId,
            ProfileName = profile.Name,
            Provider    = profile.Provider,
            Schema      = schema,
            Table       = table,
        });
    }

    // ── Table data JSON endpoint (POST with JSON body + antiforgery header) ──

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Schema/TableDataJson/{profileId:guid}")]
    public async Task<IActionResult> TableDataJson(
        Guid profileId,
        [FromBody] TableDataRequest request,
        CancellationToken ct)
    {
        try
        {
            var opened = await OpenForCurrentUserAsync(profileId, ct);
            if (opened is null) return NotFound();
            var (profile, conn) = opened.Value;

            await using (conn)
            {
                var filters = request.Filters.Select(f => new ColumnFilter(
                    f.Column,
                    Enum.TryParse<FilterOp>(f.Op, ignoreCase: true, out var op) ? op : FilterOp.Equals,
                    f.Value)).ToList();

                var query = new TableQuery(
                    Math.Max(1, request.Page),
                    request.PageSize,
                    request.OrderBy,
                    request.Descending,
                    filters);

                var dataReader = _tableDataReaderFactory.Create(profile.Provider, conn);
                var page = await dataReader.ReadPageAsync(request.Schema, request.Table, query, ct);
                return Json(page);
            }
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<DbConnectionProfile?> FindProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        var userId = _userManager.GetUserId(User)!;
        return await _db.ConnectionProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId && (p.OwnerId == userId || p.IsShared), ct);
    }

    private async Task<(DbConnectionProfile profile, DbConnection conn)?> OpenForCurrentUserAsync(
        Guid profileId, CancellationToken ct)
    {
        var profile = await FindProfileAsync(profileId, ct);
        if (profile is null) return null;

        var plaintext = _protector.Unprotect(profile.EncryptedConnectionString);
        var factory = _providerFactory.GetFactory(profile.Provider);
        var conn = factory.CreateConnection()!;
        conn.ConnectionString = plaintext;
        await conn.OpenAsync(ct);

        profile.LastUsedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return (profile, conn);
    }
}
