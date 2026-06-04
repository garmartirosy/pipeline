using DBMonitor.Data;
using DBMonitor.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DBMonitor.Controllers;

[Authorize]
public class SettingsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly SignInManager<IdentityUser> _signIn;

    public SettingsController(
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager,
        SignInManager<IdentityUser> signIn)
    {
        _db          = db;
        _userManager = userManager;
        _signIn      = signIn;
    }

    public async Task<IActionResult> Index()
    {
        var prefs = await GetOrCreatePrefsAsync();
        return View(prefs);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(UserPreferences model)
    {
        var prefs = await GetOrCreatePrefsAsync();
        prefs.DefaultPageSize            = Math.Clamp(model.DefaultPageSize, 10, 1000);
        prefs.DefaultQueryTimeout        = Math.Clamp(model.DefaultQueryTimeout, 1, 300);
        prefs.DefaultMaxRows             = Math.Clamp(model.DefaultMaxRows, 10, 100_000);
        prefs.ConfirmDestructiveByDefault = model.ConfirmDestructiveByDefault;
        prefs.UpdatedUtc                 = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Settings saved.";
        return RedirectToAction(nameof(Index));
    }

    // ── Privacy actions ───────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAuditLog()
    {
        var userId = _userManager.GetUserId(User)!;
        await _db.QueryAuditEntries.Where(a => a.OwnerId == userId).ExecuteDeleteAsync();
        TempData["Success"] = "Audit log cleared.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSavedQueries()
    {
        var userId = _userManager.GetUserId(User)!;
        await _db.SavedQueries.Where(sq => sq.OwnerId == userId).ExecuteDeleteAsync();
        TempData["Success"] = "Saved queries deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAccount()
    {
        var user   = await _userManager.GetUserAsync(User);
        if (user is null) return NotFound();

        var userId = user.Id;

        await _db.QueryAuditEntries.Where(a => a.OwnerId == userId).ExecuteDeleteAsync();
        await _db.SavedQueries.Where(sq => sq.OwnerId == userId).ExecuteDeleteAsync();
        await _db.ConnectionProfiles.Where(p => p.OwnerId == userId).ExecuteDeleteAsync();
        await _db.UserPreferences.Where(p => p.OwnerId == userId).ExecuteDeleteAsync();

        await _signIn.SignOutAsync();
        await _userManager.DeleteAsync(user);

        return RedirectToAction("Index", "Home");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task<UserPreferences> GetOrCreatePrefsAsync()
    {
        var userId = _userManager.GetUserId(User)!;
        var prefs  = await _db.UserPreferences.FindAsync(userId);
        if (prefs is null)
        {
            prefs = new UserPreferences { OwnerId = userId };
            _db.UserPreferences.Add(prefs);
            await _db.SaveChangesAsync();
        }
        return prefs;
    }
}

