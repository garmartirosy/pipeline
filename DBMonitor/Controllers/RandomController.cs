using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel;
using System.Diagnostics;

namespace DBMonitor.Controllers;

[Authorize]
public class RandomController : Controller
{
    private const int ScriptTimeoutMs = 30_000;

    private readonly IWebHostEnvironment _env;

    public RandomController(IWebHostEnvironment env) => _env = env;

    public IActionResult Index() => View();

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Insert(CancellationToken ct)
    {
        // NOTE: scripts path assumes the PythonScripts folder sits one level above
        // ContentRootPath (i.e. next to the web project in the solution). Move to
        // IConfiguration if the deployment layout ever changes.
        var scriptPath = Path.GetFullPath(
            Path.Combine(_env.ContentRootPath, "..", "PythonScripts", "insert_db.py"));

        foreach (var exe in new[] { "py", "python", "python3" })
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = $"\"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            try
            {
                using var proc = Process.Start(psi)!;

                // Read both streams concurrently to prevent pipe-buffer deadlock
                using var timeoutCts = new CancellationTokenSource(ScriptTimeoutMs);
                using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(linkedCts.Token);
                var stderrTask = proc.StandardError.ReadToEndAsync(linkedCts.Token);
                await Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync(linkedCts.Token));

                var output = await stdoutTask;
                var error  = await stderrTask;

                if (proc.ExitCode == 0)
                    return Json(new { success = true,  message = output.Trim() });

                return Json(new { success = false, message = error.Trim() });
            }
            catch (Win32Exception)
            {
                continue; // executable not found — try next candidate
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        return Json(new { success = false, message = "Python is not installed or not on PATH." });
    }
}
