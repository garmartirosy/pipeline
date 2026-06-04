using System.Text;

namespace DBMonitor.Services.Import;

public record CsvInspection(
    char Delimiter,
    bool HasHeader,
    Encoding Encoding,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> PreviewRows,
    long EstimatedRowCount);
