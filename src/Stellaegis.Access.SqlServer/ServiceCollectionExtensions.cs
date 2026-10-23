using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Access.SqlServer;

/// <summary>Registers the SQL Server stores of the rights.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server stores of the four domains, their options bound from <paramref name="sectionName"/>,
    /// along with the decision and the projection of the module
    /// (<see cref="Access.ServiceCollectionExtensions.AddStellaegisAccess(IServiceCollection)"/>). The stores declare
    /// their database, <see cref="AccessDatabase.Value"/>, which a host verifies at startup under the application's
    /// role with <c>AddStellaegisDatabaseVerification()</c>, and which an installer installs under an administration
    /// login.
    /// </summary>
    public static IServiceCollection AddStellaegisAccessSqlServer(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = AccessDatabaseOptions.SectionName)
    {
        Guard.NotNull(configuration);
        Guard.NotWhiteSpace(sectionName);

        services.AddStellaegisOptions<AccessDatabaseOptions>(configuration, sectionName);
        return services.AddSqlServerStores();
    }

    /// <summary>
    /// Registers the SQL Server stores of the four domains and the decision and projection of the module, their
    /// options configured in code; otherwise as
    /// <see cref="AddStellaegisAccessSqlServer(IServiceCollection, IConfiguration, string)"/>.
    /// </summary>
    public static IServiceCollection AddStellaegisAccessSqlServer(this IServiceCollection services, Action<AccessDatabaseOptions> configure)
    {
        Guard.NotNull(configure);

        services.AddStellaegisOptions(configure);
        return services.AddSqlServerStores();
    }

    private static IServiceCollection AddSqlServerStores(this IServiceCollection services)
    {
        services.TryAddSingleton<SqlServerAccessStore>();
        services.TryAddSingleton<IMembershipStore>(provider => provider.GetRequiredService<SqlServerAccessStore>());
        services.TryAddSingleton<IUserGrantStore>(provider => provider.GetRequiredService<SqlServerAccessStore>());
        services.TryAddSingleton<IAdminGrantStore>(provider => provider.GetRequiredService<SqlServerAccessStore>());
        services.TryAddSingleton<IClientGrantStore>(provider => provider.GetRequiredService<SqlServerAccessStore>());
        services.AddStellaegisModuleDatabase(AccessDatabase.Value,
            (provider, cancellationToken) => provider.GetRequiredService<SqlServerAccessStore>().OpenConnectionAsync(cancellationToken));
        return services.AddStellaegisAccess();
    }
}
