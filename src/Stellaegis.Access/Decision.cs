using Microsoft.Extensions.Logging;
using Stellaegis.Primitives;

namespace Stellaegis.Access;

/// <summary>
/// The decision point: an actor, a space, an operation — yes or no. Four steps, each monotone: the actor is admitted
/// in the space, the actor holds the operation in the space, the session proved a second factor when the right
/// requires one, and when a client acts on the actor's behalf (<see cref="Actor.Via"/>) the client holds the
/// operation there too. A refusal carries one of the codes of <see cref="AccessErrors"/>. The decision reads the
/// rights a registry projected; it validates nothing against a catalogue, and knows neither accounts, nor tokens, nor
/// roles: whether the session proved a second factor is told by the caller, which read the token.
/// </summary>
public interface IAccessDecision
{
    /// <summary>
    /// Decides for an actor, whose subject is read from its form (<see cref="AccessSubject.Of"/>): a GUID is a
    /// person, anything else a client. The <see cref="Actor.Via"/>, when present, is the client acting on the
    /// actor's behalf, and must hold the operation in the space as well. <paramref name="secondFactor"/> says whether
    /// the session of the person proved a second factor — an <c>amr</c> holding <c>mfa</c>.
    /// </summary>
    ValueTask<Result> DecideAsync(Actor actor, Space space, Operation operation, bool secondFactor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides for a subject the caller has named — a resource server that read a <c>client_id</c> claim knows
    /// the kind better than the form of the value says —, <paramref name="via"/> being the client acting on its
    /// behalf, or <see langword="null"/>, and <paramref name="secondFactor"/> whether the session of the person
    /// proved a second factor.
    /// </summary>
    ValueTask<Result> DecideAsync(AccessSubject subject, AccessSubject? via, Space space, Operation operation, bool secondFactor, CancellationToken cancellationToken = default);
}

/// <summary>The decision on the three stores of the domains.</summary>
internal sealed class AccessDecision(
    IMembershipStore memberships,
    IUserGrantStore userGrants,
    IAdminGrantStore adminGrants,
    IClientGrantStore clientGrants,
    ILogger<AccessDecision> logger) : IAccessDecision
{
    public ValueTask<Result> DecideAsync(Actor actor, Space space, Operation operation, bool secondFactor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(actor);
        return DecideAsync(AccessSubject.Of(actor), actor.Via is { } via ? AccessSubject.Of(via) : null, space, operation, secondFactor, cancellationToken);
    }

    public async ValueTask<Result> DecideAsync(AccessSubject subject, AccessSubject? via, Space space, Operation operation, bool secondFactor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(subject);
        Guard.NotNull(space);
        Guard.NotNull(operation);

        // 1. Admitted in the space: fails closed whatever the operation.
        if (!await IsAdmittedAsync(subject, space, cancellationToken).ConfigureAwait(false))
            return Refuse(AccessErrors.NotMember, subject, via, space, operation);

        // 2. Holds the operation in the space, read from the domain of the subject.
        var held = await FindAsync(subject, space, operation.Identity, cancellationToken).ConfigureAwait(false);
        if (held is null)
            return Refuse(AccessErrors.Denied, subject, via, space, operation);

        // 3. The session proved a second factor when the right requires one. Said only to whoever holds the operation.
        if (held.RequiresSecondFactor && !secondFactor)
            return Refuse(AccessErrors.SecondFactorRequired, subject, via, space, operation);

        // 4. The client acting on the subject's behalf holds it too: an intersection, never a union. A via is a
        //    client whatever its form says — a person has no business acting through another person.
        if (via is not null && !await clientGrants.HoldsAsync(via.Id, space, operation.Identity, cancellationToken).ConfigureAwait(false))
            return Refuse(AccessErrors.ViaDenied, subject, via, space, operation);

        return Result.Success();
    }

    private async Task<bool> IsAdmittedAsync(AccessSubject subject, Space space, CancellationToken cancellationToken)
    {
        if (subject.Kind == SubjectKind.Client)
            return await clientGrants.HasAnyAsync(subject.Id, space, cancellationToken).ConfigureAwait(false);
        return space.Compartment is { } compartment
            ? await memberships.IsMemberAsync(subject.Id, compartment, cancellationToken).ConfigureAwait(false)
            : await adminGrants.HasAnyAsync(subject.Id, cancellationToken).ConfigureAwait(false);
    }

    // A client has no second factor: its rights are operations, never sensitive to a session it does not have.
    private async Task<AccessGrant?> FindAsync(AccessSubject subject, Space space, string operation, CancellationToken cancellationToken)
    {
        if (subject.Kind == SubjectKind.Client)
            return await clientGrants.HoldsAsync(subject.Id, space, operation, cancellationToken).ConfigureAwait(false) ? new AccessGrant(operation) : null;
        return space.Compartment is { } compartment
            ? await userGrants.FindAsync(subject.Id, compartment, operation, cancellationToken).ConfigureAwait(false)
            : await adminGrants.FindAsync(subject.Id, operation, cancellationToken).ConfigureAwait(false);
    }

    private Result Refuse(Error error, AccessSubject subject, AccessSubject? via, Space space, Operation operation)
    {
        logger.LogInformation("Access refused ({Code}): {Kind} {Subject}{Via} in {Space} for {Operation}.",
            error.Code, subject.Kind, subject.Id, via is null ? "" : $" via {via.Id}", space, operation.Identity);
        return Result.Failure(error);
    }
}
