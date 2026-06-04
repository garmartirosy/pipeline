using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text;

namespace DBMonitor.Services.Import;

public class CsvInspector : ICsvInspector
{
    private const int SniffBytes  = 65_536; // 64 KB for delimiter detection
    private const int PreviewRows = 50;
    private const long MaxRows    = 5_000_000;

    public Task<CsvInspection> InspectAsync(string filePath, CancellationToken ct = default)
        => InspectAsync(filePath, null, null, null, ct);

    public async Task<CsvInspection> InspectAsync(
        string filePath,
        char? delimiterOverride,
        bool? hasHeaderOverride,
        string? encodingNameOverride,
        CancellationToken ct = default)
    {
        var encoding = ResolveEncoding(filePath, encodingNameOverride);
        var delimiter = delimiterOverride ?? await SniffDelimiterAsync(filePath, encoding, ct);
        var hasHeader = hasHeaderOverride ?? true;

        var (headers, preview, estimatedRows) = await ReadPreviewAsync(
            filePath, encoding, delimiter, hasHeader, ct);

        return new CsvInspection(delimiter, hasHeader, encoding, headers, preview, estimatedRows);
    }

    // ── Encoding detection ─────────────────────────────────────────────────────

    private static Encoding ResolveEncoding(string filePath, string? nameOverride)
    {
        if (!string.IsNullOrEmpty(nameOverride))
        {
            return nameOverride.ToLowerInvariant() switch
            {
                "utf-8" or "utf8"         => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                "utf-16" or "unicode"     => Encoding.Unicode,
                "windows-1252" or "1252"  => Encoding.GetEncoding(1252),
                _                         => Encoding.GetEncoding(nameOverride),
            };
        }

        // BOM sniff
        using var fs = File.OpenRead(filePath);
        var bom = new byte[4];
        var read = fs.Read(bom, 0, 4);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            return Encoding.Unicode; // UTF-16 LE
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); // default UTF-8 without BOM
    }

    // ── Delimiter sniffing ────────────────────────────────────────────────────

    private static async Task<char> SniffDelimiterAsync(
        string filePath, Encoding encoding, CancellationToken ct)
    {
        // Read up to 64 KB, then count candidate delimiters per line.
        // The delimiter that appears most consistently (low variance, high count) wins.
        var candidates = new[] { ',', ';', '\t', '|' };
        var countsPerLine = candidates.ToDictionary(c => c, _ => new List<int>());

        await using var fs = File.OpenRead(filePath);
        using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);

        var totalRead = 0;
        string? line;
        while (totalRead < SniffBytes && (line = await reader.ReadLineAsync(ct)) is not null)
        {
            totalRead += line.Length + 2; // rough byte estimate
            foreach (var c in candidates)
            {
                int count = 0;
                bool inQuote = false;
                foreach (char ch in line)
                {
                    if (ch == '"') inQuote = !inQuote;
                    else if (!inQuote && ch == c) count++;
                }
                countsPerLine[c].Add(count);
            }
        }

        // Score each candidate: sum of counts weighted by consistency across lines.
        char best = ',';
        double bestScore = -1;
        foreach (var c in candidates)
        {
            var counts = countsPerLine[c];
            if (counts.Count == 0) continue;
            double avg = counts.Average();
            if (avg < 1) continue;
            // Prefer high average with low variance
            double variance = counts.Select(x => Math.Pow(x - avg, 2)).Average();
            double score = avg / (1 + variance);
            if (score > bestScore) { bestScore = score; best = c; }
        }

        return best;
    }

    // ── Preview + row count estimate ──────────────────────────────────────────

    private static async Task<(IReadOnlyList<string> headers,
                                IReadOnlyList<IReadOnlyList<string>> rows,
                                long estimated)>
        ReadPreviewAsync(
            string filePath,
            Encoding encoding,
            char delimiter,
            bool hasHeader,
            CancellationToken ct)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter           = delimiter.ToString(),
            HasHeaderRecord     = hasHeader,
            MissingFieldFound   = null,
            BadDataFound        = null,
            IgnoreBlankLines    = false,
            DetectDelimiter     = false,
        };

        await using var fs = File.OpenRead(filePath);
        using var textReader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(textReader, config);

        List<string> headers;
        if (hasHeader)
        {
            await csv.ReadAsync();
            csv.ReadHeader();
            headers = csv.HeaderRecord?.ToList() ?? new List<string>();
            // Strip BOM from first header if encoding didn't strip it
            if (headers.Count > 0 && headers[0].StartsWith('﻿'))
                headers[0] = headers[0][1..];
        }
        else
        {
            // Peek at first row to determine field count, generate Col0, Col1, …
            if (await csv.ReadAsync())
            {
                headers = Enumerable.Range(0, csv.Parser.Count)
                    .Select(i => $"Column{i}")
                    .ToList();
                // Rewind is not possible with a stream; re-open for preview
                // Instead, include this row in the preview directly.
                var firstRow = Enumerable.Range(0, csv.Parser.Count)
                    .Select(i => csv.GetField(i) ?? "")
                    .ToList();

                var moreRows = new List<IReadOnlyList<string>> { firstRow };
                while (moreRows.Count < PreviewRows && await csv.ReadAsync())
                {
                    moreRows.Add(Enumerable.Range(0, headers.Count)
                        .Select(i => csv.GetField(i) ?? "")
                        .ToList());
                }

                var estimated = EstimateRowCount(filePath, encoding);
                return (headers, moreRows, estimated);
            }
            headers = new List<string>();
            return (headers, Array.Empty<IReadOnlyList<string>>(), 0);
        }

        var rows = new List<IReadOnlyList<string>>();
        while (rows.Count < PreviewRows && await csv.ReadAsync())
        {
            rows.Add(Enumerable.Range(0, headers.Count)
                .Select(i => csv.GetField(i) ?? "")
                .ToList());
        }

        var est = EstimateRowCount(filePath, encoding);
        return (headers, rows, est);
    }

    private static long EstimateRowCount(string filePath, Encoding encoding)
    {
        // Estimate by counting newlines in the file.
        // Rough: read first 8 KB to get average line length, then scale by file size.
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length == 0) return 0;

            using var fs = File.OpenRead(filePath);
            var sampleBytes = new byte[Math.Min(8192, (int)info.Length)];
            int read = fs.Read(sampleBytes, 0, sampleBytes.Length);
            long newlines = sampleBytes.Take(read).Count(b => b == 0x0A);
            if (newlines == 0) return 1;

            double bytesPerLine = (double)read / newlines;
            return (long)(info.Length / bytesPerLine);
        }
        catch
        {
            return 0;
        }
    }
}
