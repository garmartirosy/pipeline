namespace DBMonitor.Models.ViewModels;

public record FetchScriptRequest(string Url);
public record RunScriptRequest(string RawUrl, List<ScriptArg>? Args, List<string>? Packages);
public record InstallPackagesRequest(List<string> Packages);
public record ScriptArg(string Name, string? Value);
public record ParsedArg(string Name, bool Required, string Help);
