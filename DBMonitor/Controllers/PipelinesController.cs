using DBMonitor.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DBMonitor.Controllers;

[Authorize]
public partial class PipelinesController : Controller
{
    private const string UserAgent       = "DBMonitor/1.0";
    private const int    ScriptTimeoutMs = 60_000;

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "raw.githubusercontent.com",
        "gist.githubusercontent.com",
    };

    private readonly IHttpClientFactory _http;

    public PipelinesController(IHttpClientFactory http) => _http = http;

    public IActionResult Index() => View();

    // ── Fetch script source + auto-detect argparse arguments ─────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> FetchScript(
        [FromBody] FetchScriptRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Url))
            return Json(new { error = "URL is required." });

        if (!TryBuildRawUrl(req.Url.Trim(), out var rawUrl, out var validationError))
            return Json(new { error = validationError });

        try
        {
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            var content = await client.GetStringAsync(rawUrl, ct);
            var args    = ParseArgparseArgs(content);
            return Json(new { content, args, rawUrl });
        }
        catch (Exception ex)
        {
            return Json(new { error = $"Could not fetch script: {ex.Message}" });
        }
    }

    // ── Install pip packages on demand ───────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> InstallPackages(
        [FromBody] InstallPackagesRequest req, CancellationToken ct)
    {
        var packages = (req.Packages ?? [])
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (packages.Count == 0)
            return Json(new { success = false, output = "No packages specified." });

        var pythonExe = FindPythonExe();
        if (pythonExe is null)
            return Json(new { success = false, output = "Python is not installed or not on PATH." });

        var outputParts = new List<string>();

        var ensureOutput = await EnsurePipAsync(pythonExe, ct);
        if (!string.IsNullOrEmpty(ensureOutput))
            outputParts.Add(ensureOutput);

        var pkgList = string.Join(" ", packages.Select(p => $"\"{p}\""));
        var (stdout, stderr, exitCode) = await RunProcessAsync(pythonExe, $"-m pip install {pkgList}", ct);
        var installOutput = string.Concat(stdout, stderr.Length > 0 ? "\n" + stderr : "").Trim();
        outputParts.Add(installOutput);

        return Json(new { success = exitCode == 0, output = string.Join("\n", outputParts).Trim() });
    }

    // ── Run pipeline script with caller-supplied arguments ────────────────────

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunScript(
        [FromBody] RunScriptRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RawUrl))
            return Json(new { success = false, output = "No script URL provided." });

        if (!TryBuildRawUrl(req.RawUrl.Trim(), out var rawUrl, out var validationError))
            return Json(new { success = false, output = validationError });

        var pythonExe = FindPythonExe();
        if (pythonExe is null)
            return Json(new { success = false, output = "Python is not installed or not on PATH." });

        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.py");
        try
        {
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            var content = await client.GetStringAsync(rawUrl, ct);
            await System.IO.File.WriteAllTextAsync(tempPath, content, ct);

            var outputParts = new List<string>();

            // Install requested packages before running the script
            var packages = (req.Packages ?? [])
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            if (packages.Count > 0)
            {
                var ensureOutput = await EnsurePipAsync(pythonExe, ct);
                if (!string.IsNullOrEmpty(ensureOutput))
                    outputParts.Add($"[ensurepip]\n{ensureOutput}");

                var pkgList = string.Join(" ", packages.Select(p => $"\"{p}\""));
                var (pipOut, pipErr, pipCode) = await RunProcessAsync(
                    pythonExe, $"-m pip install {pkgList}", ct);

                var pipOutput = string.Concat(pipOut, pipErr.Length > 0 ? "\n" + pipErr : "").Trim();
                if (!string.IsNullOrEmpty(pipOutput))
                    outputParts.Add($"[pip install]\n{pipOutput}");

                if (pipCode != 0)
                {
                    outputParts.Add("[pip install failed — script not executed]");
                    return Json(new { success = false, output = string.Join("\n\n", outputParts) });
                }

                outputParts.Add(string.Empty); // blank line separator
            }

            var argParts = (req.Args ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .Select(a => $"--{a.Name} \"{(a.Value ?? "").Replace("\"", "\\\"")}\"");
            var argStr = string.Join(" ", argParts);

            var (stdout, stderr, exitCode) = await RunProcessAsync(
                pythonExe, $"\"{tempPath}\" {argStr}", ct);

            var scriptOutput = string.Concat(stdout, stderr.Length > 0 ? "\n" + stderr : "").Trim();
            outputParts.Add(scriptOutput);

            return Json(new { success = exitCode == 0, output = string.Join("\n", outputParts).Trim() });
        }
        finally
        {
            try { System.IO.File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
    }

    // ── Process helpers ───────────────────────────────────────────────────────

    private static async Task<string> EnsurePipAsync(string pythonExe, CancellationToken ct)
    {
        // Try the standard bootstrap first
        var (ensureOut, ensureErr, ensureCode) = await RunProcessAsync(pythonExe, "-m ensurepip --upgrade", ct);
        if (ensureCode == 0)
            return string.Concat(ensureOut, ensureErr.Length > 0 ? "\n" + ensureErr : "").Trim();

        // Debian/Ubuntu strips ensurepip from system python3 — install via apt-get instead
        var (aptOut, aptErr, aptCode) = await RunProcessAsync(
            "/bin/bash", "-c \"apt-get install -y python3-pip 2>&1\"", ct);
        var aptOutput = string.Concat(aptOut, aptErr.Length > 0 ? "\n" + aptErr : "").Trim();
        if (aptCode == 0)
            return aptOutput;

        return $"[ensurepip] {string.Concat(ensureOut, ensureErr).Trim()}\n[apt-get] {aptOutput}";
    }

    private static string? FindPythonExe()
    {
        foreach (var exe in new[] { "py", "python", "python3" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo
                {
                    FileName               = exe,
                    Arguments              = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                });
                probe?.WaitForExit();
                return exe;
            }
            catch (Win32Exception) { continue; }
        }
        return null;
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunProcessAsync(
        string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc       = Process.Start(psi)!;
        using var timeoutCts = new CancellationTokenSource(ScriptTimeoutMs);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // Read both streams concurrently to prevent pipe-buffer deadlock
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(linkedCts.Token);
        await Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync(linkedCts.Token));

        return (await stdoutTask, await stderrTask, proc.ExitCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryBuildRawUrl(string url, out string rawUrl, out string error)
    {
        rawUrl = string.Empty;
        error  = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "URL must be an absolute HTTPS address.";
            return false;
        }

        if (!AllowedHosts.Contains(uri.Host))
        {
            error = $"Host '{uri.Host}' is not allowed. Supported: {string.Join(", ", AllowedHosts)}.";
            return false;
        }

        // Convert GitHub UI blob URL to raw content URL
        rawUrl = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? url.Replace("https://github.com/", "https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
                 .Replace("/blob/", "/", StringComparison.OrdinalIgnoreCase)
            : url;

        return true;
    }

    private static readonly Regex ArgNameRegex  = new(@"^\s*\(\s*[""']--([\w_-]+)[""']", RegexOptions.Compiled);
    private static readonly Regex RequiredRegex = new(@"required\s*=\s*True",             RegexOptions.Compiled);
    private static readonly Regex HelpTextRegex = new(@"help\s*=\s*[""']([^""']*)[""']", RegexOptions.Compiled);

    private static List<ParsedArg> ParseArgparseArgs(string script)
    {
        var results = new List<ParsedArg>();

        foreach (var block in ExtractAddArgumentBlocks(script))
        {
            var nameMatch = ArgNameRegex.Match(block);
            if (!nameMatch.Success) continue;

            var name     = nameMatch.Groups[1].Value;
            var required = RequiredRegex.IsMatch(block);
            var helpM    = HelpTextRegex.Match(block);
            var help     = helpM.Success ? helpM.Groups[1].Value : string.Empty;

            results.Add(new ParsedArg(name, required, help));
        }

        return results;
    }

    private static IEnumerable<string> ExtractAddArgumentBlocks(string script)
    {
        const string marker = "add_argument";
        var i = 0;

        while (i < script.Length)
        {
            var idx = script.IndexOf(marker, i, StringComparison.Ordinal);
            if (idx < 0) yield break;

            var paren = script.IndexOf('(', idx + marker.Length);
            if (paren < 0) yield break;

            var depth = 0;
            var j     = paren;
            while (j < script.Length)
            {
                if      (script[j] == '(') depth++;
                else if (script[j] == ')') { depth--; if (depth == 0) { j++; break; } }
                j++;
            }

            yield return script[paren..j];
            i = j;
        }
    }
}
