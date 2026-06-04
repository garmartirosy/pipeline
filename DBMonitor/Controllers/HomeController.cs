using DBMonitor.Data;
using DBMonitor.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace DBMonitor.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;

    public HomeController(
        ILogger<HomeController> logger,
        ApplicationDbContext db,
        UserManager<IdentityUser> userManager)
    {
        _logger      = logger;
        _db          = db;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            var userId = _userManager.GetUserId(User)!;
            var recent = await _db.ConnectionProfiles
                .Where(p => (p.OwnerId == userId || p.IsShared) && p.LastUsedUtc != null)
                .OrderByDescending(p => p.LastUsedUtc)
                .Take(5)
                .ToListAsync();
            ViewBag.RecentProfiles = recent;
        }
        return View();
    }

    public IActionResult Privacy() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
