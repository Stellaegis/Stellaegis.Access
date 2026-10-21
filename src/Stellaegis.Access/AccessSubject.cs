using Stellaegis.Primitives;

namespace Stellaegis.Access;

/// <summary>What kind of subject a right is granted to, which decides the domain its rights are read from.</summary>
public enum SubjectKind
{
    /// <summary>
    /// A person: the <c>sub</c> of an account, a GUID. In a compartment, rights come from the memberships and the user
    /// grants; in the shared space, from the admin grants.
    /// </summary>
    Person,

    /// <summary>
    /// A client: the <c>client_id</c> of a machine-to-machine client, or the <c>job:</c> actor of a piece of work.
    /// Its rights come from the client grants, keyed by the space.
    /// </summary>
    Client,
}

/// <summary>
/// The subject of a decision or of a projection: an actor's value with the kind of subject it is. A right is
/// granted to a subject in a space; the <see cref="Actor.Via"/> of an actor is a second subject, decided apart.
/// </summary>
/// <param name="Kind">Whether the subject is a person or a client.</param>
/// <param name="Id">The subject's identifier: the <c>sub</c> of a person, the <c>client_id</c> of a client.</param>
public sealed record AccessSubject(SubjectKind Kind, string Id)
{
    /// <summary>A person, by the <c>sub</c> of their account.</summary>
    public static AccessSubject Person(string id) => new(SubjectKind.Person, Guard.NotWhiteSpace(id));

    /// <summary>A person, by the identity of their account.</summary>
    public static AccessSubject Person(Guid userUniqueId) => new(SubjectKind.Person, Guard.NotEmpty(userUniqueId).ToString("N"));

    /// <summary>A client, by its <c>client_id</c>.</summary>
    public static AccessSubject Client(string clientId) => new(SubjectKind.Client, Guard.NotWhiteSpace(clientId));

    /// <summary>
    /// The subject of an actor, by its form: an actor whose value is a GUID is a person — the <c>sub</c> an account
    /// issues —, any other value is a client: a <c>client_id</c>, or a <c>job:</c> actor. The rule reads the value
    /// alone, never the <see cref="Actor.Via"/>; a caller that knows better — a resource server reading a
    /// <c>client_id</c> claim — names the kind itself with <see cref="Person(string)"/> or <see cref="Client"/>.
    /// </summary>
    public static AccessSubject Of(Actor actor)
    {
        Guard.NotNull(actor);
        return Guid.TryParse(actor.Value, out var id) ? Person(id) : Client(actor.Value);
    }

    /// <summary>The subject as an actor, by its identifier.</summary>
    public Actor ToActor() => Actor.Parse(Id);
}
