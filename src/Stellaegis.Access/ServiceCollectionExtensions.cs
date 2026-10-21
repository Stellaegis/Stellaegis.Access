using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Access.InMemory;
using Stellaegis.Primitives;

namespace Stellaegis.Access;

/// <summary>Registers the access module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the decision (<see cref="IAccessDecision"/>) and the projection (<see cref="IAccessProjection"/>)
    /// over the four stores of the domains, and declares the module to the application. The stores come from an
    /// adapter: <see cref="AddStellaegisAccessInMemory"/>, or <c>Stellaegis.Access.PostgreSQL</c>, which call this.
    /// Events go to the registered <see cref="IEventPublisher"/>, or nowhere when the host registers none.
    /// </summary>
    public static IServiceCollection AddStellaegisAccess(this IServiceCollection services)
    {
        Guard.NotNull(services);
        services.AddStellaegisAccessModule();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEventPublisher>(NullEventPublisher.Instance);
        services.TryAddScoped<IAccessDecision, AccessDecision>();
        services.TryAddScoped<IAccessProjection, AccessProjection>();
        return services;
    }

    /// <summary>
    /// Registers the in-memory stores along with the decision and the projection. For development and tests only:
    /// the rights are lost with the process.
    /// </summary>
    public static IServiceCollection AddStellaegisAccessInMemory(this IServiceCollection services)
    {
        Guard.NotNull(services);
        services.TryAddSingleton<InMemoryAccessData>();
        services.TryAddSingleton<IMembershipStore, InMemoryMembershipStore>();
        services.TryAddSingleton<IUserGrantStore, InMemoryUserGrantStore>();
        services.TryAddSingleton<IAdminGrantStore, InMemoryAdminGrantStore>();
        services.TryAddSingleton<IClientGrantStore, InMemoryClientGrantStore>();
        return services.AddStellaegisAccess();
    }
}
