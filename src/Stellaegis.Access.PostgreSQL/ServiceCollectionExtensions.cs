using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Access.PostgreSQL;

/// <summary>Registers the PostgreSQL stores of the rights.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL stores of the four domains, their options bound from <paramref name="sectionName"/>,
    /// along with the decision and the projection of the module
    /// (<see cref="Access.ServiceCollectionExtensions.AddStellaegisAccess(IServiceCollection)"/>). The stores declare
    /// their database, <see cref="AccessDatabase.Value"/>, which a host verifies at startup under the application's
    /// role with <c>AddStellaegisDatabaseVerification()</c>, and which an installer installs under an administration
    /// role.
    /// </summary>
    public static IServiceCollection AddStellaegisAccessPostgreSQL(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = AccessDatabaseOptions.SectionName)
    {
        Guard.NotNull(configuration);
        Guard.NotWhiteSpace(sectionName);

        services.AddStellaegisOptions<AccessDatabaseOptions>(configuration, sectionName);
        return services.AddPostgreSqlStores();
    }

    /// <summary>
    /// Registers the PostgreSQL stores of the four domains and the decision and projection of the module, their
    /// options configured in code; otherwise as
    /// <see cref="AddStellaegisAccessPostgreSQL(IServiceCollection, IConfiguration, string)"/>.
    /// </summary>
    public static IServiceCollection AddStellaegisAccessPostgreSQL(this IServiceCollection services, Action<AccessDatabaseOptions> configure)
    {
        Guard.NotNull(configure);

        services.AddStellaegisOptions(configure);
        return services.AddPostgreSqlStores();
    }

    private static IServiceCollection AddPostgreSqlStores(this IServiceCollection services)
    {
        services.TryAddSingleton<PostgreSqlAccessStore>();
        services.TryAddSingleton<IMembershipStore>(provider => provider.GetRequiredService<PostgreSqlAccessStore>());
        services.TryAddSingleton<IUserGrantStore>(provider => provider.GetRequiredService<PostgreSqlAccessStore>());
        services.TryAddSingleton<IAdminGrantStore>(provider => provider.GetRequiredService<PostgreSqlAccessStore>());
        services.TryAddSingleton<IClientGrantStore>(provider => provider.GetRequiredService<PostgreSqlAccessStore>());
        services.AddStellaegisModuleDatabase(AccessDatabase.Value,
            (provider, cancellationToken) => provider.GetRequiredService<PostgreSqlAccessStore>().OpenConnectionAsync(cancellationToken));
        return services.AddStellaegisAccess();
    }
}
