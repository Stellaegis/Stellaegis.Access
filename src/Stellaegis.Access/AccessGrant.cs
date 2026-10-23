using Stellaegis.Primitives;

namespace Stellaegis.Access;

/// <summary>
/// A right of a person, as a registry projects it: an operation, by its identity <c>target.name</c>, and whether the
/// decision refuses it to a session that did not prove a second factor. The requirement belongs to the operation —
/// its module declares it, an installation hardens it —, never to the role that grants it; the registry says it with
/// each right, and Access keeps it beside the right.
/// </summary>
/// <param name="Operation">The identity of the operation, <c>target.name</c>.</param>
/// <param name="RequiresSecondFactor">Whether the session must have proven a second factor for the decision to say yes.</param>
public sealed record AccessGrant(string Operation, bool RequiresSecondFactor = false)
{
    /// <summary>The grant of <paramref name="operation"/>, with the requirement its module declares.</summary>
    public static AccessGrant Of(Operation operation) => new(Guard.NotNull(operation).Identity, operation.RequiresSecondFactor);

    /// <summary>
    /// The grants, one per operation: an operation projected twice requires a second factor when either says so — the
    /// stricter wins.
    /// </summary>
    public static IReadOnlyList<AccessGrant> Merge(IEnumerable<AccessGrant> grants)
        => Guard.NotNull(grants)
            .GroupBy(grant => grant.Operation, StringComparer.Ordinal)
            .Select(group => new AccessGrant(group.Key, group.Any(grant => grant.RequiresSecondFactor)))
            .ToList();
}
