namespace DBMonitor.Services.Import;

public interface ICsvInspector
{
    Task<CsvInspection> InspectAsync(string filePath, CancellationToken ct = default);

    Task<CsvInspection> InspectAsync(
        string filePath,
        char? delimiterOverride,
        bool? hasHeaderOverride,
        string? encodingNameOverride,
        CancellationToken ct = default);
}
