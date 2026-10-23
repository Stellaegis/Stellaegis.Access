using Stellaegis.Primitives;

namespace Stellaegis.Access;

/// <summary>
/// Where the memberships of persons are kept: which compartments a person is admitted in. One row per
/// <c>(subject, compartment)</c>, replaced by snapshot: the registry says which compartments a person belongs to,
/// never which one to add. Each method is one atomic write.
/// </summary>
public interface IMembershipStore
{
    /// <summary>Whether the person is admitted in the compartment.</summary>
    Task<bool> IsMemberAsync(string subject, Compartment compartment, CancellationToken cancellationToken = default);

    /// <summary>The compartments the person is admitted in.</summary>
    Task<IReadOnlyList<Compartment>> ListAsync(string subject, CancellationToken cancellationToken = default);

    /// <summary>Replaces the memberships of the person by <paramref name="compartments"/>; empty removes them all.</summary>
    Task ReplaceAsync(string subject, IReadOnlyCollection<Compartment> compartments, CancellationToken cancellationToken = default);

    /// <summary>Removes every membership of the compartment, once it is terminated.</summary>
    Task RemoveCompartmentAsync(Compartment compartment, CancellationToken cancellationToken = default);

    /// <summary>Removes every membership of the person, once the account is gone.</summary>
    Task RemoveSubjectAsync(string subject, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where the rights of persons in compartments are kept: one row per <c>(subject, compartment, operation)</c>, the
/// operation by its identity, <c>target.name</c>, with whether it requires a second factor. Replaced by snapshot, a
/// compartment at a time.
/// </summary>
public interface IUserGrantStore
{
    /// <summary>The right of the person on the operation in the compartment, or <see langword="null"/> when they do not hold it.</summary>
    Task<AccessGrant?> FindAsync(string subject, Compartment compartment, string operation, CancellationToken cancellationToken = default);

    /// <summary>The rights of the person in the compartment, by operation identity.</summary>
    Task<IReadOnlyList<AccessGrant>> ListAsync(string subject, Compartment compartment, CancellationToken cancellationToken = default);

    /// <summary>Replaces the rights of the person in the compartment by <paramref name="grants"/>, one per operation; empty removes them all.</summary>
    Task ReplaceAsync(string subject, Compartment compartment, IReadOnlyCollection<AccessGrant> grants, CancellationToken cancellationToken = default);

    /// <summary>Removes every right granted in the compartment, once it is terminated.</summary>
    Task RemoveCompartmentAsync(Compartment compartment, CancellationToken cancellationToken = default);

    /// <summary>Removes every right of the person, in every compartment.</summary>
    Task RemoveSubjectAsync(string subject, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where the rights of the operators of the platform are kept: one row per <c>(subject, operation)</c>, the shared
/// space implicit, with whether it requires a second factor. A person with any row here is admitted in the shared
/// space. Replaced by snapshot.
/// </summary>
public interface IAdminGrantStore
{
    /// <summary>The right of the person on the operation in the shared space, or <see langword="null"/> when they do not hold it.</summary>
    Task<AccessGrant?> FindAsync(string subject, string operation, CancellationToken cancellationToken = default);

    /// <summary>Whether the person holds any operation in the shared space — is an operator of the platform.</summary>
    Task<bool> HasAnyAsync(string subject, CancellationToken cancellationToken = default);

    /// <summary>The rights of the person in the shared space, by operation identity.</summary>
    Task<IReadOnlyList<AccessGrant>> ListAsync(string subject, CancellationToken cancellationToken = default);

    /// <summary>Replaces the rights of the person in the shared space by <paramref name="grants"/>, one per operation; empty removes them all.</summary>
    Task ReplaceAsync(string subject, IReadOnlyCollection<AccessGrant> grants, CancellationToken cancellationToken = default);

    /// <summary>Removes every right of the person in the shared space.</summary>
    Task RemoveSubjectAsync(string subject, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where the rights of clients are kept: one row per <c>(client, space, operation)</c>, the space a compartment or
/// the shared space. A client with any row in a space is admitted in it: a client of a compartment has rights in
/// its own compartment and nowhere else, a client of the platform in the shared space. Replaced by snapshot, a
/// space at a time.
/// </summary>
public interface IClientGrantStore
{
    /// <summary>Whether the client holds the operation in the space.</summary>
    Task<bool> HoldsAsync(string clientId, Space space, string operation, CancellationToken cancellationToken = default);

    /// <summary>Whether the client holds any operation in the space — is admitted in it.</summary>
    Task<bool> HasAnyAsync(string clientId, Space space, CancellationToken cancellationToken = default);

    /// <summary>The operations the client holds in the space, by identity.</summary>
    Task<IReadOnlyList<string>> ListAsync(string clientId, Space space, CancellationToken cancellationToken = default);

    /// <summary>Replaces the rights of the client in the space by <paramref name="operations"/>; empty removes them all.</summary>
    Task ReplaceAsync(string clientId, Space space, IReadOnlyCollection<string> operations, CancellationToken cancellationToken = default);

    /// <summary>Removes every right granted in the compartment, once it is terminated.</summary>
    Task RemoveCompartmentAsync(Compartment compartment, CancellationToken cancellationToken = default);

    /// <summary>Removes every right of the client, in every space.</summary>
    Task RemoveSubjectAsync(string clientId, CancellationToken cancellationToken = default);
}
