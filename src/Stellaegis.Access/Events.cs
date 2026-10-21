using Stellaegis.Primitives;

namespace Stellaegis.Access;

/// <summary>
/// Published when the rights of a subject in a space were replaced by a projection. A resource server apart that
/// caches decisions drops what it holds for that subject in that space; the host connects the subscription.
/// </summary>
public sealed record GrantsChangedEvent : IEvent
{
    /// <inheritdoc/>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The subject whose rights changed.</summary>
    public required AccessSubject Subject { get; init; }

    /// <summary>The compartment the rights are in, or <see langword="null"/> for the shared space.</summary>
    public required Compartment? Compartment { get; init; }
}

/// <summary>Published when the memberships of a person were replaced by a projection.</summary>
public sealed record MembershipsChangedEvent : IEvent
{
    /// <inheritdoc/>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The person, by the <c>sub</c> of their account.</summary>
    public required string Subject { get; init; }
}

/// <summary>Published when a compartment was removed: every membership and right granted in it is gone.</summary>
public sealed record CompartmentRemovedEvent : IEvent
{
    /// <inheritdoc/>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The compartment removed.</summary>
    public required Compartment Compartment { get; init; }
}

/// <summary>Published when a subject was removed: every membership and right of it, in every space, is gone.</summary>
public sealed record SubjectRemovedEvent : IEvent
{
    /// <inheritdoc/>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The subject removed.</summary>
    public required AccessSubject Subject { get; init; }
}
