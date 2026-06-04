using DBMonitor.Models;
using System.Data.Common;
using System.Diagnostics;

namespace DBMonitor.Services;

public class ConnectionTester : IConnectionTester
{
    private readonly IDbProviderFactory _providerFactory;

    public ConnectionTester(IDbProviderFactory providerFactory)
    {
        _providerFactory = providerFactory;
    }

    public async Task<ConnectionTestResult> TestAsync(
        DbProviderKind provider,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var factory = _providerFactory.GetFactory(provider);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = factory.CreateConnection()
                ?? throw new InvalidOperationException("Provider returned a null connection.");
            conn.ConnectionString = connectionString;
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(cancellationToken);

            sw.Stop();
            return new ConnectionTestResult(true, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionTestResult(false, sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
