namespace DBMonitor.Services.Import;

public enum NullHandling
{
    EmptyAsNull,
    EmptyAsEmptyString,
    LiteralNullToken,   // "NULL" or "\N" in the CSV cell → DB NULL
}
