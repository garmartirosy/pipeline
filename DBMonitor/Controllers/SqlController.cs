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
using System.Text;

namespace DBMonitor.Controllers;

[Authorize]
public class SqlController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IConnectionStringProtector _protector;
    private readonly IDbProviderFactory _providerFactory;
    private readonly IQueryExecutor _executor;
    private readonly SchemaReaderFactory _schemaReaderFactory;
    private readonly ProcedureExecutorFactory _procExecutorFactory;
    private readonly UserManager<IdentityUser> _userManager;

    public SqlController(
        ApplicationDbContext db,
        IConnectionStringProtector protector,
        IDbProviderFactory providerFactory,
        IQueryExecutor executor,
        SchemaReaderFactory schemaReaderFactory,
        ProcedureExecutorFactory procExecutorFactory,
        UserManager<IdentityUser> userManager)
    {
        _db = db;
        _protector = protector;
        _providerFactory = providerFactory;
        _executor = executor;
        _schemaReaderFactory = schemaReaderFactory;
        _procExecutorFactory = procExecutorFactory;
        _userManager = userManager;
    }

    // ── Editor shell ──────────────────────────────────────────────────────────

    [HttpGet]
    [Route("Sql/Editor/{profileId:guid}")]
    public async Task<IActionResult> Editor(Guid profileId, CancellationToken ct)
    {
        var userId = _userManager.GetUserId(User)!;
        var profile = await _db.ConnectionProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId && (p.OwnerId == userId || p.IsShared), ct);
        if (profile is null) return NotFound();

        return View(new BrowseVm
        {
            ProfileId   = profileId,
            ProfileName = profile.Name,
            Provider    = profile.Provider,
        });
    }

    // ── Execute (POST JSON) ───────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Sql/Execute/{profileId:guid}")]
    public async Task<IActionResult> Execute(
        Guid profileId,
        [FromBody] SqlExecuteRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Sql))
            return Json(new { error = "SQL is empty." });

        var userId  = _userManager.GetUserId(User)!;
        var profile = await _db.ConnectionProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId && (p.OwnerId == userId || p.IsShared), ct);
        if (profile is null) return NotFound();

        var plaintext = _protector.Unprotect(profile.EncryptedConnectionString);
        var factory   = _providerFactory.GetFactory(profile.Provider);
        var conn      = factory.CreateConnection()!;
        conn.ConnectionString = plaintext;

        var audit = new QueryAuditEntry
        {
            OwnerId   = userId,
            ProfileId = profileId,
            Sql       = req.Sql.Length <= 8000 ? req.Sql : req.Sql[..8000],
        };

        try
        {
            await conn.OpenAsync(ct);
            await using (conn)
            {
                var qreq = new QueryRequest(
                    profileId,
                    req.Sql,
                    req.TimeoutSeconds,
                    req.MaxRows,
                    req.AllowDestructive);

                var result = await _executor.ExecuteAsync(conn, qreq, ct);

                audit.ElapsedMs       = result.ElapsedMs;
                audit.RecordsAffected = result.RecordsAffected;
                audit.RolledBack      = result.RolledBack;
                audit.Success         = true;

                _db.QueryAuditEntries.Add(audit);
                await _db.SaveChangesAsync(CancellationToken.None);

                return Json(new
                {
                    elapsedMs       = result.ElapsedMs,
                    recordsAffected = result.RecordsAffected,
                    rolledBack      = result.RolledBack,
                    message         = result.Message,
                    resultSets      = result.ResultSets.Select((rs, idx) => new
                    {
                        index     = idx,
                        columns   = rs.Columns.Select(c => c.Name),
                        rows      = rs.Rows,
                        truncated = rs.Truncated,
                        rowCount  = rs.Rows.Count,
                    }),
                });
            }
        }
        catch (Exception ex)
        {
            audit.Success      = false;
            audit.ErrorMessage = ex.Message.Length <= 2000 ? ex.Message : ex.Message[..2000];
            audit.ElapsedMs    = 0;

            _db.QueryAuditEntries.Add(audit);
            await _db.SaveChangesAsync(CancellationToken.None);

            return Json(new { error = ex.Message });
        }
    }

    // ── History (GET JSON) ────────────────────────────────────────────────────

    [HttpGet]
    [Route("Sql/History/{profileId:guid}")]
    public async Task<IActionResult> History(Guid profileId, CancellationToken ct)
    {
        var userId = _userManager.GetUserId(User)!;

        var entries = await _db.QueryAuditEntries
            .Where(e => e.OwnerId == userId && e.ProfileId == profileId)
            .OrderByDescending(e => e.ExecutedUtc)
            .Take(50)
            .Select(e => new
            {
                e.Id,
                e.ExecutedUtc,
                e.Sql,
                e.Success,
                e.RolledBack,
                e.ElapsedMs,
                e.RecordsAffected,
                e.ErrorMessage,
            })
            .ToListAsync(ct);

        return Json(entries);
    }

    // ── Procedure shell (GET) ─────────────────────────────────────────────────

    [HttpGet]
    [Route("Sql/Procedure/{profileId:guid}")]
    public async Task<IActionResult> Procedure(
        Guid profileId,
        [FromQuery] string schema,
        [FromQuery] string name,
        CancellationToken ct)
    {
        var userId  = _userManager.GetUserId(User)!;
        var profile = await _db.ConnectionProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId && (p.OwnerId == userId || p.IsShared), ct);
        if (profile is null) return NotFound();

        var plaintext = _protector.Unprotect(profile.EncryptedConnectionString);
        var factory   = _providerFactory.GetFactory(profile.Provider);
        var conn      = factory.CreateConnection()!;
        conn.ConnectionString = plaintext;

        RoutineInfo routine;
        await conn.OpenAsync(ct);
        await using (conn)
        {
            var reader = _schemaReaderFactory.Create(profile.Provider, conn);
            routine = await reader.GetRoutineAsync(schema, name, ct);
        }

        // Build an EXEC/CALL template for the "Open as SQL" link so users can refine it in the editor.
        var execSql = BuildExecTemplate(schema, name, routine, profile.Provider);
        var execEditorUrl = Url.Action("Editor", "Sql", new { profileId })
            + "?sql=" + Uri.EscapeDataString(execSql);

        return View(new ProcedureVm
        {
            ProfileId    = profileId,
            ProfileName  = profile.Name,
            Provider     = profile.Provider,
            Schema       = schema,
            Name         = name,
            Routine      = routine,
            ExecEditorUrl = execEditorUrl,
        });
    }

    // ── Execute procedure (POST JSON) ─────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Sql/ExecuteProcedure/{profileId:guid}")]
    public async Task<IActionResult> ExecuteProcedure(
        Guid profileId,
        [FromBody] ProcedureExecuteRequest req,
        CancellationToken ct)
    {
        var userId  = _userManager.GetUserId(User)!;
        var profile = await _db.ConnectionProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId && (p.OwnerId == userId || p.IsShared), ct);
        if (profile is null) return NotFound();

        // Audit SQL: values are INTENTIONALLY REDACTED to avoid logging secrets (passwords, PII, etc.).
        // The audit row records which procedure ran and which parameters were supplied, not their values.
        // Do NOT change this to include actual values — future "harmonization" with the SQL editor
        // would be wrong here because proc params frequently carry sensitive data.
        var auditSql = BuildAuditSql(req.Schema, req.Name, req.Parameters, profile.Provider);

        var audit = new QueryAuditEntry
        {
            OwnerId   = userId,
            ProfileId = profileId,
            Sql       = auditSql.Length <= 8000 ? auditSql : auditSql[..8000],
        };

        var plaintext = _protector.Unprotect(profile.EncryptedConnectionString);
        var factory   = _providerFactory.GetFactory(profile.Provider);
        var conn      = factory.CreateConnection()!;
        conn.ConnectionString = plaintext;

        try
        {
            await conn.OpenAsync(ct);
            await using (conn)
            {
                var procReq = new ProcedureRequest(
                    profileId,
                    req.Schema,
                    req.Name,
                    req.Parameters.Select(p => new ProcedureParameterValue(
                        p.Name, p.RawValue, p.IsNull, p.UseDefault)).ToList(),
                    req.TimeoutSeconds,
                    req.MaxRows);

                var procExec = _procExecutorFactory.Create(profile.Provider);
                var result   = await procExec.ExecuteAsync(conn, procReq, ct);

                audit.ElapsedMs       = result.ElapsedMs;
                audit.RecordsAffected = result.RecordsAffected;
                audit.Success         = true;

                _db.QueryAuditEntries.Add(audit);
                await _db.SaveChangesAsync(CancellationToken.None);

                return Json(new
                {
                    elapsedMs       = result.ElapsedMs,
                    recordsAffected = result.RecordsAffected,
                    returnValue     = result.ReturnValue,
                    outputValues    = result.OutputValues,
                    message         = result.Message,
                    resultSets      = result.ResultSets.Select((rs, idx) => new
                    {
                        index     = idx,
                        columns   = rs.Columns.Select(c => c.Name),
                        rows      = rs.Rows,
                        truncated = rs.Truncated,
                        rowCount  = rs.Rows.Count,
                    }),
                });
            }
        }
        catch (Exception ex)
        {
            audit.Success      = false;
            audit.ErrorMessage = ex.Message.Length <= 2000 ? ex.Message : ex.Message[..2000];
            audit.ElapsedMs    = 0;

            _db.QueryAuditEntries.Add(audit);
            await _db.SaveChangesAsync(CancellationToken.None);

            return Json(new { error = ex.Message });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildExecTemplate(string schema, string name, RoutineInfo routine, DbProviderKind provider)
    {
        if (provider == DbProviderKind.SqlServer)
        {
            var sb = new StringBuilder();
            sb.Append($"EXEC [{schema}].[{name}]");
            if (routine.Parameters.Count > 0)
            {
                sb.AppendLine();
                var parts = routine.Parameters.Select(p => $"    {p.Name} = NULL");
                sb.Append(string.Join(",\n", parts));
            }
            return sb.ToString();
        }
        else
        {
            var args = string.Join(", ", routine.Parameters.Select(p => $"{p.Name} => NULL"));
            return $"CALL \"{schema}\".\"{name}\"({args})";
        }
    }

    // Produces a redacted audit string: param values replaced with <…> or <OUTPUT>.
    private static string BuildAuditSql(
        string schema, string name,
        IEnumerable<ProcedureParameterValueDto> parameters,
        DbProviderKind provider)
    {
        var parts = parameters.Select(p =>
        {
            // Tag: distinguish NULL, DEFAULT, OUTPUT-declared, and value-supplied cases —
            // but never include the actual value.
            var tag = p.UseDefault ? "<DEFAULT>"
                    : p.IsNull    ? "<NULL>"
                    : "<…>"; // <…>
            return provider == DbProviderKind.SqlServer
                ? $"{p.Name} = {tag}"
                : $"{p.Name.TrimStart('@')} => {tag}";
        });

        return provider == DbProviderKind.SqlServer
            ? $"EXEC [{schema}].[{name}] {string.Join(", ", parts)}"
            : $"CALL \"{schema}\".\"{name}\"({string.Join(", ", parts)})";
    }
}
