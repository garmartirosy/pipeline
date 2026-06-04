using System.Data.Common;

namespace DBMonitor.Services.Query;

public static class RowProjector
{
    public static IReadOnlyList<object?> ProjectRow(DbDataReader reader)
    {
        var cells = new object?[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
            cells[i] = ProjectValue(reader.GetValue(i));
        return cells;
    }

    // Public so ProcedureExecutor can project output parameter values with the same rules.
    public static object? ProjectValue(object? raw)
    {
        if (raw is null or DBNull)     return null;
        if (raw is byte[] bytes)       return $"(binary, {bytes.Length} bytes)";
        // Stringify decimal to prevent JS Number precision loss for large values.
        if (raw is decimal d)          return d.ToString("G");
        if (raw is DateTime dt)        return dt.ToString("O");
        if (raw is DateTimeOffset dto) return dto.ToString("O");
        if (raw is Guid g)             return g.ToString();
        return raw; // int, long, double, float, bool, string — serialise as-is
    }
}
