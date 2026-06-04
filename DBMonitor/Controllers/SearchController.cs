using DBMonitor.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DBMonitor.Controllers;

[Authorize]
public class SearchController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;

    public SearchController(ApplicationDbContext db, UserManager<IdentityUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    [HttpGet]
    public async Task<IActionResult> Results(string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return View(new SearchResultsVm(q ?? "", [], [], []));

        var userId = _userManager.GetUserId(User)!;
        var term   = q.Trim();

        var profiles = await _db.ConnectionProfiles
            .Where(p => (p.OwnerId == userId || p.IsShared) && p.Name.Contains(term))
            .OrderBy(p => p.Name)
            .Take(20)
            .Select(p => new SearchHit(p.Id.ToString(), p.Name, "Connection", null,
                "/Connections/Details/" + p.Id))
            .ToListAsync();

        var saved = await _db.SavedQueries
            .Where(sq => sq.OwnerId == userId &&
                         (sq.Name.Contains(term) || sq.Sql.Contains(term) ||
                          (sq.Description != null && sq.Description.Contains(term))))
            .OrderBy(sq => sq.Name)
            .Take(20)
            .Select(sq => new SearchHit(sq.Id.ToString(), sq.Name, "Saved Query",
                sq.Description, "/SavedQueries/Edit/" + sq.Id))
            .ToListAsync();

        var history = await _db.QueryAuditEntries
            .Where(a => a.OwnerId == userId && a.Sql.Contains(term))
            .OrderByDescending(a => a.ExecutedUtc)
            .Take(20)
            .Select(a => new SearchHit(a.Id.ToString(),
                a.Sql.Length > 80 ? a.Sql.Substring(0, 80) + "…" : a.Sql,
                "History", a.ExecutedUtc.ToString("yyyy-MM-dd HH:mm") + " UTC",
                null))
            .ToListAsync();

        return View(new SearchResultsVm(term, profiles, saved, history));
    }
}

public record SearchHit(string Id, string Title, string Category, string? Subtitle, string? Url);
public record SearchResultsVm(
    string Query,
    IReadOnlyList<SearchHit> Profiles,
    IReadOnlyList<SearchHit> SavedQueries,
    IReadOnlyList<SearchHit> History);
