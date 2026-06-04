using DBMonitor.Models;
using CommonDbProviderFactory = System.Data.Common.DbProviderFactory;

namespace DBMonitor.Services;

public interface IDbProviderFactory
{
    CommonDbProviderFactory GetFactory(DbProviderKind provider);
}
