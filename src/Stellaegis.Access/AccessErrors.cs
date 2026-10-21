using Stellaegis.Primitives;

namespace Stellaegis.Access;

/// <summary>
/// The errors a decision answers, each with the code that lets the caller recognize it: in process through a
/// failed <see cref="Result"/>, over HTTP through the <c>code</c> of the problem details. A refusal never says
/// which right would have been needed: the message is the same whatever the operation.
/// </summary>
public static class AccessErrors
{
    /// <summary>The code of <see cref="NotMember"/>: the actor is not admitted in the space, whatever the operation.</summary>
    public const string NotMemberCode = "Access.NotMember";

    /// <summary>The code of <see cref="Denied"/>: the actor is admitted in the space but does not hold the operation there.</summary>
    public const string DeniedCode = "Access.Denied";

    /// <summary>The code of <see cref="ViaDenied"/>: the actor holds the operation, but the client acting on its behalf does not.</summary>
    public const string ViaDeniedCode = "Access.ViaDenied";

    /// <summary>
    /// The actor is not admitted in the space: a person without a membership of the compartment, a person without any
    /// right in the shared space, a client without any right in the space. The first step of a decision, which fails
    /// closed whatever the operation.
    /// </summary>
    public static Error NotMember { get; } = Error.Forbidden(NotMemberCode, "You are not admitted in this space.");

    /// <summary>The actor is admitted in the space but does not hold the operation there.</summary>
    public static Error Denied { get; } = Error.Forbidden(DeniedCode, "You are not allowed to perform this operation.");

    /// <summary>
    /// The actor holds the operation, but the client acting on its behalf (<see cref="Actor.Via"/>) does not hold it
    /// in the space: the rights of a person are restricted by those of the client acting for them, never widened.
    /// </summary>
    public static Error ViaDenied { get; } = Error.Forbidden(ViaDeniedCode, "The client acting on your behalf is not allowed to perform this operation.");
}
