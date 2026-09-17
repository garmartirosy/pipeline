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
    private const int    PipTimeoutMs    = 180_000;

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
            var content  = await client.GetStringAsync(rawUrl, ct);
            var args     = ParseArgparseArgs(content);
            var packages = ParseImports(content);
            return Json(new { content, args, rawUrl, packages });
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
        var (stdout, stderr, exitCode) = await RunProcessAsync(pythonExe, $"-m pip install --break-system-packages {pkgList}", ct);
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
                    pythonExe, $"-m pip install --break-system-packages {pkgList}", ct);

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

    private async Task<string> EnsurePipAsync(string pythonExe, CancellationToken ct)
    {
        // 1. Standard bootstrap (works on most Python installs)
        var (ensureOut, ensureErr, ensureCode) = await RunProcessAsync(
            pythonExe, "-m ensurepip --upgrade", ct, PipTimeoutMs);
        if (ensureCode == 0)
            return string.Concat(ensureOut, ensureErr.Length > 0 ? "\n" + ensureErr : "").Trim();

        // 2. Debian/Ubuntu strips ensurepip — refresh index and install python3-pip
        var (aptOut, aptErr, aptCode) = await RunProcessAsync(
            "/bin/bash", "-c \"apt-get update -qq && apt-get install -y python3-pip 2>&1\"", ct, PipTimeoutMs);
        if (aptCode == 0)
            return string.Concat(aptOut, aptErr.Length > 0 ? "\n" + aptErr : "").Trim();

        // 3. Official PyPA get-pip.py — works on any Python 3 without system packages
        var getPipPath = Path.Combine(Path.GetTempPath(), "get-pip.py");
        try
        {
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            var script = await client.GetStringAsync("https://bootstrap.pypa.io/get-pip.py", ct);
            await System.IO.File.WriteAllTextAsync(getPipPath, script, ct);
            var (pipOut, pipErr, _) = await RunProcessAsync(pythonExe, getPipPath, ct, PipTimeoutMs);
            return string.Concat(pipOut, pipErr.Length > 0 ? "\n" + pipErr : "").Trim();
        }
        finally
        {
            try { System.IO.File.Delete(getPipPath); } catch { }
        }
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
        string exe, string args, CancellationToken ct, int timeoutMs = ScriptTimeoutMs)
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
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
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

    // Top-level Python 3 standard-library modules — anything else is treated
    // as a pip dependency. Not exhaustive; covers what we expect users to import.
    private static readonly HashSet<string> StdLibModules = new(StringComparer.Ordinal)
    {
        "__future__", "__main__", "abc", "argparse", "array", "ast", "asyncio",
        "base64", "bisect", "builtins", "bz2", "calendar", "collections",
        "concurrent", "configparser", "contextlib", "copy", "csv", "ctypes",
        "dataclasses", "datetime", "decimal", "difflib", "dis", "email", "enum",
        "errno", "faulthandler", "fnmatch", "fractions", "functools", "gc",
        "getopt", "getpass", "gettext", "glob", "gzip", "hashlib", "heapq",
        "hmac", "html", "http", "importlib", "inspect", "io", "ipaddress",
        "itertools", "json", "keyword", "locale", "logging", "lzma", "math",
        "mimetypes", "mmap", "multiprocessing", "netrc", "numbers", "operator",
        "os", "pathlib", "pickle", "pkgutil", "platform", "pprint", "queue",
        "random", "re", "readline", "reprlib", "secrets", "select", "selectors",
        "shelve", "shlex", "shutil", "signal", "site", "socket", "socketserver",
        "sqlite3", "ssl", "stat", "statistics", "string", "stringprep", "struct",
        "subprocess", "sys", "sysconfig", "syslog", "tempfile", "textwrap",
        "threading", "time", "timeit", "token", "tokenize", "trace", "traceback",
        "tracemalloc", "tty", "types", "typing", "unicodedata", "unittest",
        "urllib", "uuid", "warnings", "wave", "weakref", "webbrowser", "wsgiref",
        "xml", "xmlrpc", "zipfile", "zipimport", "zlib", "zoneinfo",
    };

    // Import-name → pip-package-name for modules that don't match their package.
    private static readonly Dictionary<string, string> PackageAliases = new(StringComparer.Ordinal)
    {
        ["psycopg2"] = "psycopg2-binary",
        ["cv2"]      = "opencv-python-headless",
        ["sklearn"]  = "scikit-learn",
        ["PIL"]      = "Pillow",
        ["yaml"]     = "PyYAML",
        ["bs4"]      = "beautifulsoup4",
        ["dotenv"]   = "python-dotenv",
        ["dateutil"] = "python-dateutil",
        ["MySQLdb"]  = "mysqlclient",
        ["serial"]   = "pyserial",
        ["Crypto"]   = "pycryptodome",
        ["OpenSSL"]  = "pyOpenSSL",
    };

    private static readonly Regex ImportRegex = new(
        @"^\s*(?:import\s+([\w.]+(?:\s*,\s*[\w.]+)*)|from\s+([\w.]+)\s+import\s+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static List<string> ParseImports(string script)
    {
        var modules = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in ImportRegex.Matches(script))
        {
            if (m.Groups[1].Success)
            {
                foreach (var item in m.Groups[1].Value.Split(','))
                {
                    var top = item.Trim().Split('.')[0];
                    if (top.Length > 0) modules.Add(top);
                }
            }
            else if (m.Groups[2].Success)
            {
                var top = m.Groups[2].Value.Split('.')[0];
                if (top.Length > 0) modules.Add(top);
            }
        }

        var packages = new List<string>();
        foreach (var mod in modules)
        {
            if (StdLibModules.Contains(mod)) continue;
            packages.Add(PackageAliases.TryGetValue(mod, out var alias) ? alias : mod);
        }

        packages.Sort(StringComparer.Ordinal);
        return packages;
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
