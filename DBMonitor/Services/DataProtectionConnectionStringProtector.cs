using Microsoft.AspNetCore.DataProtection;

namespace DBMonitor.Services;

public class DataProtectionConnectionStringProtector : IConnectionStringProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionConnectionStringProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("DbConnectionProfile.ConnectionString");
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
