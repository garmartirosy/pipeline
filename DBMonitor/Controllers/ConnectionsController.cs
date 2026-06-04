using DBMonitor.Data;
using DBMonitor.Models;
using DBMonitor.Models.ViewModels;
using DBMonitor.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DBMonitor.Controllers;

[Authorize]
public class ConnectionsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IConnectionStringProtector _protector;
    private readonly IConnectionTester _tester;
    private readonly UserManager<IdentityUser> _userManager;

    public ConnectionsController(
        ApplicationDbContext db,
        IConnectionStringProtector protector,
        IConnectionTester tester,
        UserManager<IdentityUser> userManager)
    {
        _db = db;
        _protector = protector;
        _tester = tester;
        _userManager = userManager;
    }

    // ── Index ────────────────────────────────────────────────────────────────

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var profiles = await _db.ConnectionProfiles
            .Where(p => p.OwnerId == userId || p.IsShared)
            .OrderByDescending(p => p.IsPinned)
            .ThenBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();
        return View(profiles);
    }

    // ── Pin / Unpin ───────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePin(Guid id)
    {
        var profile = await OwnedProfileAsync(id);
        if (profile is null) return NotFound();
        profile.IsPinned = !profile.IsPinned;
        await _db.SaveChangesAsync();
        return Json(new { isPinned = profile.IsPinned });
    }

    // ── Reorder ───────────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reorder([FromBody] ReorderRequest[] items)
    {
        if (items is null || items.Length == 0) return BadRequest();
        var userId = _userManager.GetUserId(User)!;
        var ids = items.Select(x => x.Id).ToHashSet();
        var profiles = await _db.ConnectionProfiles
            .Where(p => (p.OwnerId == userId || p.IsShared) && ids.Contains(p.Id))
            .ToListAsync();
        foreach (var p in profiles)
        {
            var match = items.FirstOrDefault(x => x.Id == p.Id);
            if (match is not null) p.SortOrder = match.SortOrder;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ── Details ──────────────────────────────────────────────────────────────

    public async Task<IActionResult> Details(Guid id)
    {
        var profile = await OwnedProfileAsync(id);
        return profile is null ? NotFound() : View(profile);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Create() => View(new ConnectionProfileFormVm());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ConnectionProfileFormVm vm)
    {
        if (string.IsNullOrWhiteSpace(vm.ConnectionString))
            ModelState.AddModelError(nameof(vm.ConnectionString), "Connection string is required.");

        if (!ModelState.IsValid)
            return View(vm);

        var userId = _userManager.GetUserId(User)!;
        _db.ConnectionProfiles.Add(new DbConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = vm.Name.Trim(),
            Provider = vm.Provider,
            EncryptedConnectionString = _protector.Protect(vm.ConnectionString!.Trim()),
            OwnerId = userId,
            CreatedUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // ── Edit ─────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var profile = await OwnedProfileAsync(id);
        if (profile is null) return NotFound();

        ViewBag.ProfileId = id;
        return View(new ConnectionProfileFormVm
        {
            Name = profile.Name,
            Provider = profile.Provider,
            KeepExistingConnectionString = true
            // ConnectionString intentionally left empty — never round-trip the secret
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, ConnectionProfileFormVm vm)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.ProfileId = id;
            return View(vm);
        }

        var profile = await OwnedProfileAsync(id);
        if (profile is null) return NotFound();

        profile.Name = vm.Name.Trim();
        profile.Provider = vm.Provider;

        if (!string.IsNullOrWhiteSpace(vm.ConnectionString))
            profile.EncryptedConnectionString = _protector.Protect(vm.ConnectionString.Trim());

        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Delete(Guid id)
    {
        var profile = await OwnedProfileAsync(id);
        return profile is null ? NotFound() : View(profile);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(Guid id)
    {
        var profile = await OwnedProfileAsync(id);
        if (profile is null) return NotFound();

        _db.ConnectionProfiles.Remove(profile);
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // ── Test: saved profile (server decrypts) ────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestSaved(Guid id, CancellationToken ct)
    {
        var profile = await OwnedProfileAsync(id, ct);
        if (profile is null) return NotFound();

        var plaintext = _protector.Unprotect(profile.EncryptedConnectionString);
        var result = await _tester.TestAsync(profile.Provider, plaintext, ct);

        if (result.Success)
        {
            profile.LastUsedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return Json(new { result.Success, result.ElapsedMs, result.ErrorMessage });
    }

    // ── Test: unsaved input from Create / Edit form ───────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestUnsaved(
        [FromForm] DbProviderKind provider,
        [FromForm] string? connectionString,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return Json(new { success = false, elapsedMs = 0L, errorMessage = "Connection string is required." });

        var result = await _tester.TestAsync(provider, connectionString.Trim(), ct);
        return Json(new { result.Success, result.ElapsedMs, result.ErrorMessage });
    }

    // ── Helpers / Records ─────────────────────────────────────────────────────

    private async Task<DbConnectionProfile?> OwnedProfileAsync(Guid id, CancellationToken ct = default)
    {
        var userId = _userManager.GetUserId(User)!;
        return await _db.ConnectionProfiles
            .FirstOrDefaultAsync(p => p.Id == id && (p.OwnerId == userId || p.IsShared), ct);
    }
}
