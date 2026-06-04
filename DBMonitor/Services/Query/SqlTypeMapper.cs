using System.Data;
using System.Globalization;

namespace DBMonitor.Services.Query;

internal static class SqlTypeMapper
{
    internal static DbType MapToDbType(string sqlTypeName) => sqlTypeName.ToLowerInvariant() switch
    {
        "int" or "integer" or "int4"                              => DbType.Int32,
        "bigint" or "int8"                                        => DbType.Int64,
        "smallint" or "int2"                                      => DbType.Int16,
        "tinyint"                                                 => DbType.Byte,
        "bit" or "boolean" or "bool"                              => DbType.Boolean,
        "decimal" or "numeric"                                    => DbType.Decimal,
        "money" or "smallmoney"                                   => DbType.Decimal,
        "float" or "float8" or "double precision"                 => DbType.Double,
        "real" or "float4"                                        => DbType.Single,
        "char" or "character"                                     => DbType.AnsiStringFixedLength,
        "nchar"                                                   => DbType.StringFixedLength,
        "varchar" or "character varying"                          => DbType.AnsiString,
        "nvarchar" or "text" or "ntext"
            or "json" or "jsonb" or "xml"                         => DbType.String,
        "date"                                                    => DbType.Date,
        "datetime" or "datetime2" or "smalldatetime"
            or "timestamp" or "timestamp without time zone"       => DbType.DateTime,
        "datetimeoffset" or "timestamp with time zone"
            or "timestamptz"                                      => DbType.DateTimeOffset,
        "time" or "time without time zone"                        => DbType.Time,
        "uniqueidentifier" or "uuid"                              => DbType.Guid,
        "binary" or "varbinary" or "image" or "bytea"             => DbType.Binary,
        // All unrecognised types fall back to String so the user can still attempt execution.
        // Extend this switch as new provider types are encountered.
        _                                                         => DbType.String,
    };

    // Parse a raw string value into the appropriate .NET type for the given DbType.
    // Throws ArgumentException with a user-readable message BEFORE any DB call is made.
    internal static object ParseRawValue(string rawValue, DbType dbType, string paramName)
    {
        try
        {
            return dbType switch
            {
                DbType.Int32 or DbType.UInt32    => int.Parse(rawValue, CultureInfo.InvariantCulture),
                DbType.Int64 or DbType.UInt64    => long.Parse(rawValue, CultureInfo.InvariantCulture),
                DbType.Int16 or DbType.UInt16    => short.Parse(rawValue, CultureInfo.InvariantCulture),
                DbType.Byte  or DbType.SByte     => byte.Parse(rawValue, CultureInfo.InvariantCulture),
                DbType.Boolean                   => ParseBool(rawValue),
                DbType.Decimal                   => decimal.Parse(rawValue, CultureInfo.InvariantCulture),
                DbType.Double                    => double.Parse(rawValue, CultureInfo.InvariantCulture),
                DbType.Single                    => float.Parse(rawValue, CultureInfo.InvariantCulture),
                DbType.Date or DbType.DateTime   => DateTime.Parse(rawValue, CultureInfo.InvariantCulture),
                DbType.DateTimeOffset            => DateTimeOffset.Parse(rawValue, CultureInfo.InvariantCulture),
                DbType.Time                      => TimeSpan.Parse(rawValue, CultureInfo.InvariantCulture),
                DbType.Guid                      => Guid.Parse(rawValue),
                DbType.Binary                    => ParseHex(rawValue, paramName),
                _                               => rawValue, // String/AnsiString/etc. pass through as-is
            };
        }
        catch (ArgumentException) { throw; }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"Parameter @{paramName}: cannot convert '{rawValue}' to {dbType}. ({ex.Message})");
        }
    }

    private static bool ParseBool(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "true"  or "1" or "yes" or "on"  => true,
        "false" or "0" or "no"  or "off" => false,
        _ => throw new FormatException($"Cannot parse '{raw}' as boolean (expected true/false/1/0)."),
    };

    // Binary params accept hex in "0x..." format (SSMS-style, more natural than base64 for SQL users).
    private static byte[] ParseHex(string hex, string paramName)
    {
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];
        if (hex.Length == 0) return Array.Empty<byte>();
        if (hex.Length % 2 != 0)
            throw new ArgumentException(
                $"Parameter @{paramName}: hex string must have an even number of digits.");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
