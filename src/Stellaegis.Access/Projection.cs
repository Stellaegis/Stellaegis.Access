using System.Diagnostics.CodeAnalysis;
using Stellaegis.Primitives;

namespace Stellaegis.Access;

/// <summary>
/// How the rights get into Access: snapshots, never deltas. A registry — in process, or through the projection
/// routes of <c>Stellaegis.Access.AspNetCore</c> — says all the memberships of a person, all the rights of a
/// subject in a space, that a compartment is terminated, that a subject is gone; Access writes it flat. The host
/// calls these from its subscriptions to the registry's events. Each projection publishes an event of the module,
/// so that the decision caches of resource servers apart drop what changed.
/// </summary>
public interface IAccessProjection
{
    /// <summary>Replaces the compartments the person is admitted in; empty removes them all.</summary>
    Task ReplaceMembershipsAsync(string subject, IReadOnlyCollection<Compartment> compartments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the rights of a person in a compartment, each with whether it requires a second factor; empty removes
    /// them all. An operation given twice requires a second factor when either says so.
    /// </summary>
    Task ReplaceUserGrantsAsync(string subject, Compartment compartment, IReadOnlyCollection<AccessGrant> grants, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the rights of a person in the shared space — what makes them an operator of the platform —, each with
    /// whether it requires a second factor; empty removes them all.
    /// </summary>
    Task ReplaceAdminGrantsAsync(string subject, IReadOnlyCollection<AccessGrant> grants, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the rights of a client in a space; empty removes them all, and the client is no longer admitted there.
    /// A client has no second factor: its rights are operations.
    /// </summary>
    Task ReplaceClientGrantsAsync(string clientId, Space space, IReadOnlyCollection<string> operations, CancellationToken cancellationToken = default);

    /// <summary>Removes every membership and every right granted in the compartment, once it is terminated.</summary>
    Task RemoveCompartmentAsync(Compartment compartment, CancellationToken cancellationToken = default);

    /// <summary>Removes every membership and every right of the subject, in every space, once it is gone.</summary>
    Task RemoveSubjectAsync(AccessSubject subject, CancellationToken cancellationToken = default);
}

/// <summary>The projection on the four stores, publishing an event after each write.</summary>
internal sealed class AccessProjection(
    IMembershipStore memberships,
    IUserGrantStore userGrants,
    IAdminGrantStore adminGrants,
    IClientGrantStore clientGrants,
    IEventPublisher publisher,
    TimeProvider time) : IAccessProjection
{
    public async Task ReplaceMembershipsAsync(string subject, IReadOnlyCollection<Compartment> compartments, CancellationToken cancellationToken = default)
    {
        Guard.NotWhiteSpace(subject);
        Guard.NotNull(compartments);
        await memberships.ReplaceAsync(subject, compartments, cancellationToken).ConfigureAwait(false);
        await publisher.PublishAsync(new MembershipsChangedEvent { OccurredAt = time.GetUtcNow(), Subject = subject }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceUserGrantsAsync(string subject, Compartment compartment, IReadOnlyCollection<AccessGrant> grants, CancellationToken cancellationToken = default)
    {
        Guard.NotWhiteSpace(subject);
        Guard.NotNull(compartment);
        await userGrants.ReplaceAsync(subject, compartment, Grants(grants), cancellationToken).ConfigureAwait(false);
        await publisher.PublishAsync(new GrantsChangedEvent { OccurredAt = time.GetUtcNow(), Subject = AccessSubject.Person(subject), Compartment = compartment }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceAdminGrantsAsync(string subject, IReadOnlyCollection<AccessGrant> grants, CancellationToken cancellationToken = default)
    {
        Guard.NotWhiteSpace(subject);
        await adminGrants.ReplaceAsync(subject, Grants(grants), cancellationToken).ConfigureAwait(false);
        await publisher.PublishAsync(new GrantsChangedEvent { OccurredAt = time.GetUtcNow(), Subject = AccessSubject.Person(subject), Compartment = null }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceClientGrantsAsync(string clientId, Space space, IReadOnlyCollection<string> operations, CancellationToken cancellationToken = default)
    {
        Guard.NotWhiteSpace(clientId);
        Guard.NotNull(space);
        await clientGrants.ReplaceAsync(clientId, space, Identities(operations), cancellationToken).ConfigureAwait(false);
        await publisher.PublishAsync(new GrantsChangedEvent { OccurredAt = time.GetUtcNow(), Subject = AccessSubject.Client(clientId), Compartment = space.Compartment }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveCompartmentAsync(Compartment compartment, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(compartment);
        await memberships.RemoveCompartmentAsync(compartment, cancellationToken).ConfigureAwait(false);
        await userGrants.RemoveCompartmentAsync(compartment, cancellationToken).ConfigureAwait(false);
        await clientGrants.RemoveCompartmentAsync(compartment, cancellationToken).ConfigureAwait(false);
        await publisher.PublishAsync(new CompartmentRemovedEvent { OccurredAt = time.GetUtcNow(), Compartment = compartment }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveSubjectAsync(AccessSubject subject, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(subject);
        if (subject.Kind == SubjectKind.Person)
        {
            await memberships.RemoveSubjectAsync(subject.Id, cancellationToken).ConfigureAwait(false);
            await userGrants.RemoveSubjectAsync(subject.Id, cancellationToken).ConfigureAwait(false);
            await adminGrants.RemoveSubjectAsync(subject.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await clientGrants.RemoveSubjectAsync(subject.Id, cancellationToken).ConfigureAwait(false);
        }
        await publisher.PublishAsync(new SubjectRemovedEvent { OccurredAt = time.GetUtcNow(), Subject = subject }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The operations as identities, each checked against the grammar of <c>target.name</c>: a projection that sends
    /// a malformed identity is a bug of the registry, refused rather than stored.
    /// </summary>
    private static IReadOnlyCollection<string> Identities(IReadOnlyCollection<string> operations)
    {
        Guard.NotNull(operations);
        foreach (var operation in operations)
            Guard.Satisfies(operation, OperationIdentities.IsIdentity, $"'{operation}' is not an operation identity: {OperationIdentities.Rule}.");
        return operations;
    }

    /// <summary>The grants checked as <see cref="Identities"/> checks their operations, one per operation, the stricter requirement kept.</summary>
    private static IReadOnlyList<AccessGrant> Grants(IReadOnlyCollection<AccessGrant> grants)
    {
        Guard.NotNull(grants);
        foreach (var grant in grants)
            Guard.Satisfies(Guard.NotNull(grant).Operation, OperationIdentities.IsIdentity, $"'{grant.Operation}' is not an operation identity: {OperationIdentities.Rule}.");
        return AccessGrant.Merge(grants);
    }
}

/// <summary>The grammar of an operation identity, <c>target.name</c>, as a right is projected and stored.</summary>
public static class OperationIdentities
{
    /// <summary>The rule, for messages.</summary>
    public const string Rule = "target.name, each 1 to 63 lowercase letters, digits and single underscores, starting with a letter and ending with a letter or a digit";

    /// <summary>Whether the value is an operation identity.</summary>
    public static bool IsIdentity([NotNullWhen(true)] string? value)
    {
        if (value is null)
            return false;
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && NameRules.IsIdentifier(value[..dot]) && NameRules.IsIdentifier(value[(dot + 1)..]);
    }
}
