using CsvHelper;
using CsvHelper.Configuration;
using DBMonitor.Data;
using DBMonitor.Models;
using DBMonitor.Services;
using DBMonitor.Services.Import;
using DBMonitor.Services.Schema;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DBMonitor.Controllers;

[Authorize]
public class ImportController : Controller
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".csv", ".tsv", ".txt" };

    private const long MaxUploadBytes = 100_000_000L; // 100 MB
    private const long MaxImportRows  = 5_000_000L;
    private const int  PreviewRowCount = 20;

    private readonly ApplicationDbContext _db;
    private readonly IConnectionStringProtector _protector;
    private readonly IDbProviderFactory _providerFactory;
    private readonly SchemaReaderFactory _schemaReaderFactory;
    private readonly IBulkImporterFactory _importerFactory;
    private readonly ICsvInspector _inspector;
    private readonly CsvSchemaInferrer _inferrer;
    private readonly UserManager<IdentityUser> _userManager;

    public ImportController(
        ApplicationDbContext db,
        IConnectionStringProtector protector,
        IDbProviderFactory providerFactory,
        SchemaReaderFactory schemaReaderFactory,
        IBulkImporterFactory importerFactory,
        ICsvInspector inspector,
        CsvSchemaInferrer inferrer,
        UserManager<IdentityUser> userManager)
    {
        _db = db;
        _protector = protector;
        _providerFactory = providerFactory;
        _schemaReaderFactory = schemaReaderFactory;
        _importerFactory = importerFactory;
        _inspector = inspector;
        _inferrer = inferrer;
        _userManager = userManager;
    }

    // ── GET /Import/Start/{profileId} ─────────────────────────────────────────

    [HttpGet]
    [Route("Import/Start/{profileId:guid}")]
    public async Task<IActionResult> Start(
        Guid profileId,
        [FromQuery] string? schema,
        [FromQuery] string? table,
        CancellationToken ct)
    {
        var profile = await OwnedProfileAsync(profileId, ct);
        if (profile is null) return NotFound();

        ViewData["ProfileId"]   = profileId;
        ViewData["ProfileName"] = profile.Name;
        ViewData["Provider"]    = profile.Provider.ToString();
        ViewData["PresetSchema"] = schema ?? "";
        ViewData["PresetTable"]  = table ?? "";
        return View();
    }

    // ── POST /Import/Upload/{profileId} ───────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Import/Upload/{profileId:guid}")]
    [RequestSizeLimit(100_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 100_000_000)]
    public async Task<IActionResult> Upload(Guid profileId, IFormFile file, CancellationToken ct)
    {
        var profile = await OwnedProfileAsync(profileId, ct);
        if (profile is null) return NotFound();

        if (file is null || file.Length == 0)
            return Json(new { error = "No file provided." });

        // Extension whitelist — content-type is not trusted
        var ext = Path.GetExtension(file.FileName);
        if (!AllowedExtensions.Contains(ext))
            return Json(new { error = $"File extension '{ext}' is not allowed. Use .csv, .tsv, or .txt." });

        if (file.Length > MaxUploadBytes)
            return Json(new { error = $"File exceeds the 100 MB limit ({file.Length:N0} bytes)." });

        // Stream to a temp file — do NOT hold in memory
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ext);
        long bytesWritten = 0;

        try
        {
            await using (var tempFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var upload = file.OpenReadStream())
            {
                var buf = new byte[81920];
                int read;
                while ((read = await upload.ReadAsync(buf, ct)) > 0)
                {
                    bytesWritten += read;
                    if (bytesWritten > MaxUploadBytes)
                    {
                        await tempFs.FlushAsync(ct);
                        System.IO.File.Delete(tempPath);
                        return Json(new { error = "File exceeds the 100 MB limit." });
                    }
                    await tempFs.WriteAsync(buf.AsMemory(0, read), ct);
                }
            }

            var inspection = await _inspector.InspectAsync(tempPath, ct);

            var userId = _userManager.GetUserId(User)!;
            var session = new ImportSession
            {
                OwnerId          = userId,
                ProfileId        = profileId,
                OriginalFileName = Path.GetFileName(file.FileName),
                TempFilePath     = tempPath,
                FileSizeBytes    = bytesWritten,
                ExpiresUtc       = DateTime.UtcNow.AddHours(1),
            };
            _db.ImportSessions.Add(session);
            await _db.SaveChangesAsync(CancellationToken.None);

            return Json(new
            {
                tempFileId = session.Id,
                inspection = SerialiseInspection(inspection),
            });
        }
        catch (Exception ex)
        {
            // Best-effort cleanup; the cleanup service will handle it if we miss
            try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { }
            return Json(new { error = ex.Message });
        }
    }

    // ── POST /Import/Inspect/{profileId} ──────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Import/Inspect/{profileId:guid}")]
    public async Task<IActionResult> Inspect(
        Guid profileId,
        [FromBody] InspectRequest req,
        CancellationToken ct)
    {
        var (profile, session) = await ResolveSessionAsync(profileId, req.TempFileId, ct);
        if (profile is null || session is null)
            return NotFound();

        try
        {
            var inspection = await _inspector.InspectAsync(
                session.TempFilePath,
                req.Delimiter == '\0' ? null : req.Delimiter,
                req.HasHeader,
                req.EncodingName,
                ct);

            return Json(SerialiseInspection(inspection));
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // ── GET /Import/TargetColumns/{profileId} ─────────────────────────────────

    [HttpGet]
    [Route("Import/TargetColumns/{profileId:guid}")]
    public async Task<IActionResult> TargetColumns(
        Guid profileId,
        [FromQuery] string schema,
        [FromQuery] string table,
        CancellationToken ct)
    {
        var profile = await OwnedProfileAsync(profileId, ct);
        if (profile is null) return NotFound();

        try
        {
            var plaintext = _protector.Unprotect(profile.EncryptedConnectionString);
            var factory   = _providerFactory.GetFactory(profile.Provider);
            var conn      = factory.CreateConnection()!;
            conn.ConnectionString = plaintext;

            await conn.OpenAsync(ct);
            await using (conn)
            {
                var reader  = _schemaReaderFactory.Create(profile.Provider, conn);
                var columns = await reader.GetImportColumnsAsync(schema, table, ct);
                return Json(columns.Select(c => new
                {
                    c.Name,
                    c.DataType,
                    c.IsNullable,
                    c.IsIdentity,
                    c.IsComputed,
                    c.HasDefault,
                    c.MaxLength,
                    c.OrdinalPosition,
                }));
            }
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // ── POST /Import/Preview/{profileId} ──────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Import/Preview/{profileId:guid}")]
    public async Task<IActionResult> Preview(
        Guid profileId,
        [FromBody] ImportExecuteRequest req,
        CancellationToken ct)
    {
        var (profile, session) = await ResolveSessionAsync(profileId, req.TempFileId, ct);
        if (profile is null || session is null)
            return NotFound();

        try
        {
            var importReq = BuildImportRequest(req, session.TempFilePath, session.OriginalFileName, profileId);
            var culture   = ResolveCulture(req.CultureName);

            var previewRows = new List<object>();
            int rowCount    = 0;

            await foreach (var produced in CsvRowProducer.ProduceAsync(
                importReq, culture, _ => { }, ct))
            {
                if (rowCount >= PreviewRowCount) break;
                rowCount++;

                var cells = new List<object?>();
                for (int i = 0; i < produced.Values.Length; i++)
                {
                    cells.Add(new { value = FormatPreviewValue(produced.Values[i]), ok = true });
                }
                previewRows.Add(new { line = produced.LineNumber, cells });
            }

            // Also collect errors for the preview rows
            var errorRows = new List<object>();
            var errCount  = 0;
            var errorsForPreview = new List<RowError>();

            // Re-run just to capture errors in preview range
            await foreach (var produced in CsvRowProducer.ProduceAsync(
                importReq, culture,
                err => { if (errCount++ < PreviewRowCount) errorsForPreview.Add(err); },
                ct))
            {
                if (errCount > PreviewRowCount) break;
            }

            var activeMappings = req.Mappings.Where(m => !m.Skip).Select(m => m.TargetColumn).ToList();
            return Json(new
            {
                columns  = activeMappings,
                rows     = previewRows,
                errors   = errorsForPreview.Select(e => new { e.LineNumber, e.CsvLine, e.Error }),
            });
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // ── POST /Import/Execute/{profileId} ──────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Import/Execute/{profileId:guid}")]
    public async Task<IActionResult> Execute(
        Guid profileId,
        [FromBody] ImportExecuteRequest req,
        CancellationToken ct)
    {
        var (profile, session) = await ResolveSessionAsync(profileId, req.TempFileId, ct);
        if (profile is null || session is null)
            return NotFound();

        // Truncate requires the user to type the table name (checked client-side, enforced here)
        if (req.ExistingDataMode == "TruncateThenInsert" &&
            !string.Equals(req.TruncateConfirmTableName, req.Table, StringComparison.OrdinalIgnoreCase))
        {
            return Json(new { error = "Table name confirmation does not match. Import aborted." });
        }

        var importReq = BuildImportRequest(req, session.TempFilePath, session.OriginalFileName, profileId);

        var plaintext = _protector.Unprotect(profile.EncryptedConnectionString);
        var factory   = _providerFactory.GetFactory(profile.Provider);
        var conn      = factory.CreateConnection()!;
        conn.ConnectionString = plaintext;

        var userId = _userManager.GetUserId(User)!;
        var auditSql = $"BULK INSERT INTO [{req.Schema}].[{req.Table}] " +
                       $"({string.Join(", ", req.Mappings.Where(m => !m.Skip).Select(m => $"[{m.TargetColumn}]"))}) " +
                       $"FROM CSV [{session.OriginalFileName}]";

        var audit = new QueryAuditEntry
        {
            OwnerId   = userId,
            ProfileId = profileId,
            Sql       = auditSql.Length <= 8000 ? auditSql : auditSql[..8000],
        };

        ImportResult result;
        try
        {
            await conn.OpenAsync(ct);
            await using (conn)
            {
                var importer = _importerFactory.Create(profile.Provider);
                result = await importer.ImportAsync(conn, importReq, ct);
            }
        }
        catch (Exception ex)
        {
            audit.Success      = false;
            audit.ErrorMessage = ex.Message.Length <= 2000 ? ex.Message : ex.Message[..2000];
            audit.ElapsedMs    = 0;
            _db.QueryAuditEntries.Add(audit);
            await _db.SaveChangesAsync(CancellationToken.None);

            // Delete temp file even on exception
            await CleanupSessionAsync(session, CancellationToken.None);
            return Json(new { error = ex.Message });
        }
        finally
        {
            // Always clean up the temp file and session row when Execute is called
            await CleanupSessionAsync(session, CancellationToken.None);
        }

        // Audit
        var firstErr = result.Errors.Count > 0
            ? $" (first error: line {result.Errors[0].LineNumber}: {result.Errors[0].Error})"
            : "";
        audit.ElapsedMs       = result.ElapsedMs;
        audit.RecordsAffected = (int)Math.Min(result.RowsInserted, int.MaxValue);
        audit.RolledBack      = result.RolledBack;
        audit.Success         = result.RowsInserted > 0 && !result.RolledBack;
        audit.ErrorMessage    = result.RowsRejected > 0
            ? $"{result.RowsRejected} rows rejected{firstErr}".Length <= 2000
                ? $"{result.RowsRejected} rows rejected{firstErr}"
                : $"{result.RowsRejected} rows rejected"
            : null;

        _db.QueryAuditEntries.Add(audit);
        await _db.SaveChangesAsync(CancellationToken.None);

        return Json(new
        {
            rowsRead       = result.RowsRead,
            rowsInserted   = result.RowsInserted,
            rowsRejected   = result.RowsRejected,
            elapsedMs      = result.ElapsedMs,
            truncated      = result.TruncatedExistingData,
            rolledBack     = result.RolledBack,
            message        = result.Message,
            errors         = result.Errors.Select(e => new
            {
                lineNumber = e.LineNumber,
                csvLine    = e.CsvLine,
                error      = e.Error,
            }),
        });
    }

    // ── POST /Import/InferSchema/{profileId} ──────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Import/InferSchema/{profileId:guid}")]
    public async Task<IActionResult> InferSchema(
        Guid profileId,
        [FromBody] InferSchemaRequest req,
        CancellationToken ct)
    {
        var (profile, session) = await ResolveSessionAsync(profileId, req.TempFileId, ct);
        if (profile is null || session is null)
            return NotFound();

        try
        {
            char delim = req.Delimiter is { Length: 1 } d ? d[0] : ',';
            var enc = string.IsNullOrEmpty(req.EncodingName) || req.EncodingName == "auto"
                ? Encoding.UTF8
                : Encoding.GetEncoding(req.EncodingName);

            var cols = await _inferrer.InferAsync(
                session.TempFilePath, delim, enc, req.HasHeader ?? true, ct);

            var sql = CsvSchemaInferrer.BuildCreateTableSql(
                cols, profile.Provider, req.Schema, req.Table);

            return Json(new
            {
                createSql = sql,
                columns   = cols.Select(c => new
                {
                    name      = c.Name,
                    csvIndex  = c.CsvIndex,
                    dataType  = SqlTypeToDataType(
                        profile.Provider == DbProviderKind.SqlServer
                            ? c.SqlServerType : c.PostgreSqlType),
                    isNullable = c.IsNullable,
                }),
            });
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // ── POST /Import/ExecuteCreate/{profileId} ────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Import/ExecuteCreate/{profileId:guid}")]
    public async Task<IActionResult> ExecuteCreate(
        Guid profileId,
        [FromBody] ExecuteCreateRequest req,
        CancellationToken ct)
    {
        var (profile, session) = await ResolveSessionAsync(profileId, req.TempFileId, ct);
        if (profile is null || session is null)
            return NotFound();

        var plaintext = _protector.Unprotect(profile.EncryptedConnectionString);
        var factory   = _providerFactory.GetFactory(profile.Provider);
        var conn      = factory.CreateConnection()!;
        conn.ConnectionString = plaintext;

        try
        {
            await conn.OpenAsync(ct);
            await using (conn)
            {
                // Execute the user-reviewed CREATE TABLE statement
                using var cmd = conn.CreateCommand();
                cmd.CommandText = req.CreateTableSql;
                await cmd.ExecuteNonQueryAsync(ct);

                // Re-infer columns to build typed mappings
                char delim = req.Delimiter is { Length: 1 } d ? d[0] : ',';
                var enc = string.IsNullOrEmpty(req.EncodingName) || req.EncodingName == "auto"
                    ? Encoding.UTF8
                    : Encoding.GetEncoding(req.EncodingName);

                var inferredCols = await _inferrer.InferAsync(
                    session.TempFilePath, delim, enc, req.HasHeader, ct);

                var mappings = inferredCols.Select(c => new ColumnMapping(
                    CsvHeader:        c.Name,
                    CsvIndex:         c.CsvIndex,
                    TargetColumn:     c.Name,
                    TargetDataType:   SqlTypeToDataType(
                        profile.Provider == DbProviderKind.SqlServer
                            ? c.SqlServerType : c.PostgreSqlType),
                    TargetIsNullable: c.IsNullable,
                    Skip:             false)).ToList();

                int batchSize = Math.Clamp(req.BatchSize, 100, 100_000);

                var importReq = new ImportRequest(
                    ProfileId:        profileId,
                    Schema:           req.Schema,
                    Table:            req.Table,
                    Mappings:         mappings,
                    Delimiter:        delim,
                    HasHeader:        req.HasHeader,
                    EncodingName:     string.IsNullOrEmpty(req.EncodingName) ? "utf-8" : req.EncodingName,
                    CultureName:      "invariant",
                    NullHandling:     NullHandling.EmptyAsNull,
                    ExistingDataMode: ExistingDataMode.Append,
                    BatchSize:        batchSize,
                    TempFilePath:     session.TempFilePath,
                    OriginalFileName: session.OriginalFileName,
                    AbortOnAnyError:  false);

                var importer = _importerFactory.Create(profile.Provider);
                var result   = await importer.ImportAsync(conn, importReq, ct);

                var userId   = _userManager.GetUserId(User)!;
                var auditSql = $"CREATE TABLE + BULK INSERT [{req.Schema}].[{req.Table}] FROM CSV [{session.OriginalFileName}]";
                var audit    = new QueryAuditEntry
                {
                    OwnerId         = userId,
                    ProfileId       = profileId,
                    Sql             = auditSql,
                    ElapsedMs       = result.ElapsedMs,
                    RecordsAffected = (int)Math.Min(result.RowsInserted, int.MaxValue),
                    RolledBack      = result.RolledBack,
                    Success         = result.RowsInserted > 0 && !result.RolledBack,
                };
                _db.QueryAuditEntries.Add(audit);
                await _db.SaveChangesAsync(CancellationToken.None);

                await CleanupSessionAsync(session, CancellationToken.None);

                return Json(new
                {
                    rowsRead     = result.RowsRead,
                    rowsInserted = result.RowsInserted,
                    rowsRejected = result.RowsRejected,
                    elapsedMs    = result.ElapsedMs,
                    truncated    = false,
                    rolledBack   = result.RolledBack,
                    message      = result.Message,
                    errors       = result.Errors.Select(e => new
                    {
                        lineNumber = e.LineNumber,
                        csvLine    = e.CsvLine,
                        error      = e.Error,
                    }),
                });
            }
        }
        catch (Exception ex)
        {
            await CleanupSessionAsync(session, CancellationToken.None);
            return Json(new { error = ex.Message });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ImportRequest BuildImportRequest(
        ImportExecuteRequest req, string tempFilePath, string originalFileName, Guid profileId)
    {
        var mappings = req.Mappings.Select(m => new ColumnMapping(
            CsvHeader:       m.CsvHeader,
            CsvIndex:        m.CsvIndex,
            TargetColumn:    m.TargetColumn,
            TargetDataType:  m.TargetDataType,
            TargetIsNullable: m.TargetIsNullable,
            Skip:            m.Skip)).ToList();

        var nullHandling = Enum.TryParse<NullHandling>(req.NullHandling, out var nh)
            ? nh : NullHandling.EmptyAsNull;

        var existingMode = Enum.TryParse<ExistingDataMode>(req.ExistingDataMode, out var em)
            ? em : ExistingDataMode.Append;

        char delim = req.Delimiter is { Length: 1 } d ? d[0] : ',';
        int batchSize = Math.Clamp(req.BatchSize, 100, 100_000);

        return new ImportRequest(
            ProfileId:        profileId,
            Schema:           req.Schema,
            Table:            req.Table,
            Mappings:         mappings,
            Delimiter:        delim,
            HasHeader:        req.HasHeader,
            EncodingName:     string.IsNullOrEmpty(req.EncodingName) ? "utf-8" : req.EncodingName,
            CultureName:      string.IsNullOrEmpty(req.CultureName)  ? "invariant" : req.CultureName,
            NullHandling:     nullHandling,
            ExistingDataMode: existingMode,
            BatchSize:        batchSize,
            TempFilePath:     tempFilePath,
            OriginalFileName: originalFileName,
            AbortOnAnyError:  req.AbortOnAnyError);
    }

    private async Task<(DbConnectionProfile? profile, ImportSession? session)> ResolveSessionAsync(
        Guid profileId, Guid tempFileId, CancellationToken ct)
    {
        var userId  = _userManager.GetUserId(User)!;
        var profile = await _db.ConnectionProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId && (p.OwnerId == userId || p.IsShared), ct);
        if (profile is null) return (null, null);

        var session = await _db.ImportSessions
            .FirstOrDefaultAsync(s => s.Id == tempFileId && s.OwnerId == userId, ct);
        if (session is null) return (null, null);

        if (session.ExpiresUtc < DateTime.UtcNow)
        {
            await CleanupSessionAsync(session, ct);
            return (null, null);
        }

        return (profile, session);
    }

    private async Task<DbConnectionProfile?> OwnedProfileAsync(Guid profileId, CancellationToken ct)
    {
        var userId = _userManager.GetUserId(User)!;
        return await _db.ConnectionProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId && (p.OwnerId == userId || p.IsShared), ct);
    }

    private async Task CleanupSessionAsync(ImportSession session, CancellationToken ct)
    {
        try { if (System.IO.File.Exists(session.TempFilePath)) System.IO.File.Delete(session.TempFilePath); }
        catch { }

        try
        {
            _db.ImportSessions.Remove(session);
            await _db.SaveChangesAsync(ct);
        }
        catch { }
    }

    private static object SerialiseInspection(CsvInspection insp) => new
    {
        delimiter      = insp.Delimiter.ToString(),
        hasHeader      = insp.HasHeader,
        encodingName   = insp.Encoding.WebName,
        headers        = insp.Headers,
        previewRows    = insp.PreviewRows,
        estimatedRows  = insp.EstimatedRowCount,
    };

    private static string FormatPreviewValue(object? v) => v switch
    {
        null              => "(null)",
        DateTime dt       => dt.ToString("O"),
        DateTimeOffset dto => dto.ToString("O"),
        byte[] bytes      => $"(binary {bytes.Length} bytes)",
        _                 => v.ToString() ?? "",
    };

    private static CultureInfo ResolveCulture(string? name) =>
        string.IsNullOrEmpty(name) || name.Equals("invariant", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(name);

    private static string SqlTypeToDataType(string sqlType)
    {
        var t = sqlType.ToLowerInvariant().Split('(')[0].Trim();
        return t switch
        {
            "int"       => "int",
            "integer"   => "integer",
            "bigint"    => "bigint",
            "decimal"   => "decimal",
            "numeric"   => "numeric",
            "bit"       => "bit",
            "boolean"   => "boolean",
            "date"      => "date",
            "datetime2" => "datetime2",
            "timestamp" => "timestamp",
            "nvarchar"  => "nvarchar",
            "varchar"   => "varchar",
            "text"      => "text",
            _           => "nvarchar",
        };
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record InspectRequest(
    Guid TempFileId,
    char Delimiter,
    bool? HasHeader,
    string? EncodingName);

public record ColumnMappingDto(
    string CsvHeader,
    int CsvIndex,
    string TargetColumn,
    string TargetDataType,
    bool TargetIsNullable,
    bool Skip);

public record ImportExecuteRequest(
    Guid TempFileId,
    string Schema,
    string Table,
    IReadOnlyList<ColumnMappingDto> Mappings,
    string Delimiter,
    bool HasHeader,
    string EncodingName,
    string CultureName,
    string NullHandling,
    string ExistingDataMode,
    int BatchSize,
    bool AbortOnAnyError,
    string? TruncateConfirmTableName = null);

public record InferSchemaRequest(
    Guid TempFileId,
    string Schema,
    string Table,
    string Delimiter,
    bool? HasHeader,
    string? EncodingName);

public record ExecuteCreateRequest(
    Guid TempFileId,
    string CreateTableSql,
    string Schema,
    string Table,
    string Delimiter,
    bool HasHeader,
    string EncodingName,
    int BatchSize);
