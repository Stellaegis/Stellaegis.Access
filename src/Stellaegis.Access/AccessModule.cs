using Microsoft.Extensions.DependencyInjection;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Access;

/// <summary>
/// The identity of the access module, <c>access</c>: what its routes belong to, and what the application declares
/// it as. The module is the decision point: an actor, a space, an operation — yes or no. It holds the effective
/// rights a registry projects into it and decides on them; it knows neither accounts, nor tokens, nor roles. It
/// has no compartments: one set of rights serves the whole application, keyed by the space each right is granted
/// in.
/// </summary>
public sealed class AccessModule : IStellaegisModule
{
    /// <inheritdoc/>
    public static string Name => "access";

    /// <summary>
    /// What the module declares to the application: no compartments, and the three operations of its own routes —
    /// asking a decision from another process, reading the rights, projecting them.
    /// </summary>
    public static ModuleDeclaration Declaration { get; } = ModuleDeclaration.WithoutCompartments<AccessModule>()
        .WithOperations(AccessOperations.All);
}

/// <summary>The operations of the access module, which its routes are labelled with.</summary>
public static class AccessOperations
{
    /// <summary>Asking a decision — the three routes a resource server apart calls, <c>decisions.read</c>.</summary>
    public static readonly Operation DecisionsRead = new("decisions", "read", OperationKind.Read);

    /// <summary>Reading the rights projected for a subject, <c>grants.read</c>.</summary>
    public static readonly Operation GrantsRead = new("grants", "read", OperationKind.Read);

    /// <summary>Projecting the rights — what a registry in another process does, <c>grants.write</c>.</summary>
    public static readonly Operation GrantsWrite = new("grants", "write", OperationKind.Write);

    /// <summary>Every operation of the module.</summary>
    public static IReadOnlyList<Operation> All { get; } = [DecisionsRead, GrantsRead, GrantsWrite];
}

/// <summary>Registers the access module itself, whatever stores hold the rights.</summary>
public static class AccessModuleServiceCollectionExtensions
{
    /// <summary>
    /// Declares the access module to the application (<see cref="AccessModule.Declaration"/>). Every store's
    /// registration calls it; calling it again registers nothing more.
    /// </summary>
    public static IServiceCollection AddStellaegisAccessModule(this IServiceCollection services)
    {
        Guard.NotNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(AccessModuleRegistered)))
            return services;
        services.AddSingleton(AccessModuleRegistered.Instance);
        return services.AddStellaegisModule(AccessModule.Declaration);
    }

    private sealed class AccessModuleRegistered
    {
        public static readonly AccessModuleRegistered Instance = new();
    }
}
