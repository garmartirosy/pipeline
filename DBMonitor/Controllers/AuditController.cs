using DBMonitor.Data;
using DBMonitor.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace DBMonitor.Controllers;

[Authorize]
public class AuditController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;

    public AuditController(ApplicationDbContext db, UserManager<IdentityUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index(
        Guid? profileId, bool? success, string? from, string? to,
        int page = 1, int pageSize = 50)
    {
        var userId = _userManager.GetUserId(User)!;
        var q = _db.QueryAuditEntries.Where(a => a.OwnerId == userId);

        if (profileId.HasValue)
            q = q.Where(a => a.ProfileId == profileId);
        if (success.HasValue)
            q = q.Where(a => a.Success == success.Value);
        if (DateTime.TryParse(from, out var fromDt))
            q = q.Where(a => a.ExecutedUtc >= fromDt.ToUniversalTime());
        if (DateTime.TryParse(to, out var toDt))
            q = q.Where(a => a.ExecutedUtc <= toDt.ToUniversalTime().AddDays(1));

        var totalCount = await q.CountAsync();
        var entries = await q
            .OrderByDescending(a => a.ExecutedUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var profiles = await _db.ConnectionProfiles
            .Where(p => p.OwnerId == userId)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync();

        ViewBag.Profiles    = profiles;
        ViewBag.ProfileId   = profileId;
        ViewBag.Success     = success;
        ViewBag.From        = from;
        ViewBag.To          = to;
        ViewBag.Page        = page;
        ViewBag.PageSize    = pageSize;
        ViewBag.TotalCount  = totalCount;
        ViewBag.TotalPages  = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));

        return View(entries);
    }

    [HttpGet]
    public async Task<IActionResult> ExportCsv(
        Guid? profileId, bool? success, string? from, string? to)
    {
        var userId = _userManager.GetUserId(User)!;
        var q = _db.QueryAuditEntries.Where(a => a.OwnerId == userId);

        if (profileId.HasValue)
            q = q.Where(a => a.ProfileId == profileId);
        if (success.HasValue)
            q = q.Where(a => a.Success == success.Value);
        if (DateTime.TryParse(from, out var fromDt))
            q = q.Where(a => a.ExecutedUtc >= fromDt.ToUniversalTime());
        if (DateTime.TryParse(to, out var toDt))
            q = q.Where(a => a.ExecutedUtc <= toDt.ToUniversalTime().AddDays(1));

        var entries = await q
            .OrderByDescending(a => a.ExecutedUtc)
            .Take(10_000)
            .ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("ExecutedUtc,ProfileId,Success,ElapsedMs,RecordsAffected,RolledBack,Sql,ErrorMessage");
        foreach (var e in entries)
        {
            sb.AppendLine(string.Join(',',
                CsvCell(e.ExecutedUtc.ToString("o")),
                CsvCell(e.ProfileId.ToString()),
                CsvCell(e.Success.ToString()),
                CsvCell(e.ElapsedMs.ToString()),
                CsvCell(e.RecordsAffected?.ToString() ?? ""),
                CsvCell(e.RolledBack.ToString()),
                CsvCell(e.Sql),
                CsvCell(e.ErrorMessage ?? "")));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", "audit-log.csv");
    }

    private static string CsvCell(string v)
    {
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
            return '"' + v.Replace("\"", "\"\"") + '"';
        return v;
    }
}
