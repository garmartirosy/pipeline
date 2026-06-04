using DBMonitor.Data;
using DBMonitor.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DBMonitor.Controllers;

[Authorize]
public class SavedQueriesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;

    public SavedQueriesController(ApplicationDbContext db, UserManager<IdentityUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    // ── Index ─────────────────────────────────────────────────────────────────

    public async Task<IActionResult> Index(Guid? profileId)
    {
        var userId = _userManager.GetUserId(User)!;
        var q = _db.SavedQueries.Where(sq => sq.OwnerId == userId);
        if (profileId.HasValue)
            q = q.Where(sq => sq.ProfileId == profileId || sq.ProfileId == null);
        var list = await q.OrderBy(sq => sq.Name).ToListAsync();
        ViewBag.ProfileId = profileId;
        return View(list);
    }

    // ── API: list (for editor sidebar) ────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List(Guid? profileId)
    {
        var userId = _userManager.GetUserId(User)!;
        var q = _db.SavedQueries.Where(sq => sq.OwnerId == userId);
        if (profileId.HasValue)
            q = q.Where(sq => sq.ProfileId == profileId || sq.ProfileId == null);
        var list = await q
            .OrderByDescending(sq => sq.LastUsedUtc)
            .ThenBy(sq => sq.Name)
            .Select(sq => new {
                sq.Id, sq.Name, sq.Description, sq.ProfileId,
                sql = sq.Sql, sq.UseCount, sq.LastUsedUtc
            })
            .ToListAsync();
        return Json(list);
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Create(Guid? profileId)
    {
        ViewBag.ProfileId = profileId;
        return View(new SavedQuery { ProfileId = profileId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SavedQuery model, Guid? profileId)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.ProfileId = profileId;
            return View(model);
        }
        var userId = _userManager.GetUserId(User)!;
        _db.SavedQueries.Add(new SavedQuery
        {
            Id          = Guid.NewGuid(),
            OwnerId     = userId,
            ProfileId   = model.ProfileId,
            Name        = model.Name.Trim(),
            Description = model.Description?.Trim(),
            Sql         = model.Sql,
            CreatedUtc  = DateTime.UtcNow,
            UpdatedUtc  = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index), new { profileId });
    }

    // ── Save (AJAX from editor) ───────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromBody] SaveQueryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Sql))
            return BadRequest(new { error = "Name and SQL are required." });

        var userId = _userManager.GetUserId(User)!;

        SavedQuery? existing = null;
        if (req.Id.HasValue)
            existing = await _db.SavedQueries.FirstOrDefaultAsync(sq => sq.Id == req.Id && sq.OwnerId == userId);

        if (existing is not null)
        {
            existing.Name        = req.Name.Trim();
            existing.Description = req.Description?.Trim();
            existing.Sql         = req.Sql;
            existing.ProfileId   = req.ProfileId;
            existing.UpdatedUtc  = DateTime.UtcNow;
        }
        else
        {
            _db.SavedQueries.Add(new SavedQuery
            {
                Id          = Guid.NewGuid(),
                OwnerId     = userId,
                ProfileId   = req.ProfileId,
                Name        = req.Name.Trim(),
                Description = req.Description?.Trim(),
                Sql         = req.Sql,
                CreatedUtc  = DateTime.UtcNow,
                UpdatedUtc  = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ── Edit ──────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var entity = await OwnedAsync(id);
        if (entity is null) return NotFound();
        ViewBag.ProfileId = entity.ProfileId;
        return View(entity);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, SavedQuery model)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.ProfileId = model.ProfileId;
            return View(model);
        }
        var entity = await OwnedAsync(id);
        if (entity is null) return NotFound();

        entity.Name        = model.Name.Trim();
        entity.Description = model.Description?.Trim();
        entity.Sql         = model.Sql;
        entity.ProfileId   = model.ProfileId;
        entity.UpdatedUtc  = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var entity = await OwnedAsync(id);
        if (entity is null) return NotFound();
        _db.SavedQueries.Remove(entity);
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // ── Run (redirect to editor with TempData) ────────────────────────────────

    public async Task<IActionResult> Run(Guid id, Guid? profileId)
    {
        var entity = await OwnedAsync(id);
        if (entity is null) return NotFound();

        entity.UseCount++;
        entity.LastUsedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["PreFillSql"] = entity.Sql;
        var targetProfile = profileId ?? entity.ProfileId;
        if (targetProfile is null) return RedirectToAction(nameof(Index));
        return RedirectToAction("Editor", "Sql", new { profileId = targetProfile });
    }

    // ── MarkUsed (AJAX) ───────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkUsed(Guid id)
    {
        var entity = await OwnedAsync(id);
        if (entity is null) return NotFound();
        entity.UseCount++;
        entity.LastUsedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task<SavedQuery?> OwnedAsync(Guid id)
    {
        var userId = _userManager.GetUserId(User)!;
        return await _db.SavedQueries.FirstOrDefaultAsync(sq => sq.Id == id && sq.OwnerId == userId);
    }
}

public record SaveQueryRequest(Guid? Id, Guid? ProfileId, string Name, string? Description, string Sql);
