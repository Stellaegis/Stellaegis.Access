using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;

namespace Stellaegis.Access.AspNetCore;

/// <summary>A decision asked for a person in a compartment.</summary>
/// <param name="Subject">The person, by the <c>sub</c> of their account.</param>
/// <param name="Compartment">The compartment the request acts in.</param>
/// <param name="Operation">The identity of the operation, <c>target.name</c>.</param>
/// <param name="Via">The client acting on the person's behalf, or <see langword="null"/>.</param>
/// <param name="SecondFactor">Whether the session of the person proved a second factor — what the caller read in the <c>amr</c> of its token.</param>
public sealed record UserDecisionRequest(string Subject, string Compartment, string Operation, string? Via = null, bool SecondFactor = false);

/// <summary>A decision asked for a person in the shared space — an operator of the platform.</summary>
/// <param name="Subject">The person, by the <c>sub</c> of their account.</param>
/// <param name="Operation">The identity of the operation, <c>target.name</c>.</param>
/// <param name="Via">The client acting on the person's behalf, or <see langword="null"/>.</param>
/// <param name="SecondFactor">Whether the session of the person proved a second factor — what the caller read in the <c>amr</c> of its token.</param>
public sealed record AdminDecisionRequest(string Subject, string Operation, string? Via = null, bool SecondFactor = false);

/// <summary>A decision asked for a client in a space.</summary>
/// <param name="Subject">The client, by its <c>client_id</c>.</param>
/// <param name="Space">The space, as a segment: a compartment, or <c>_shared</c>.</param>
/// <param name="Operation">The identity of the operation, <c>target.name</c>.</param>
/// <param name="Via">The client acting on the client's behalf, or <see langword="null"/>.</param>
/// <param name="SecondFactor">Unused for a client, which has no second factor; kept for the shape of the three questions.</param>
public sealed record ClientDecisionRequest(string Subject, string Space, string Operation, string? Via = null, bool SecondFactor = false);

/// <summary>
/// Maps the decision routes of Access, what a resource server apart calls through <c>Stellaegis.Access.Client</c>:
/// one route per domain, for the isolation of the three, the same decision behind. A yes answers 204, a refusal
/// 403 with the code of the error in the problem details, an ill-formed question 400. A question for a person says
/// whether the session proved a second factor; without it, a right that requires one is refused.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>POST access/users/decide</c> — a person in a compartment</item>
///   <item><c>POST access/admins/decide</c> — a person in the shared space</item>
///   <item><c>POST access/clients/decide</c> — a client in a space</item>
/// </list>
/// All three are labelled <c>decisions.read</c>: the host grants it, in the shared space, to the clients of the
/// resource servers that ask — Access decides who may ask Access. The module has no compartments: the routes read
/// no request context, and are mapped on a private network, called machine to machine.
/// </remarks>
public static class AccessDecisionEndpoints
{
    /// <summary>Maps the decision routes under <c>access</c>, relative to <paramref name="endpoints"/>.</summary>
    /// <returns>The group holding the routes, for the host's authorization and further conventions.</returns>
    public static RouteGroupBuilder MapAccessDecisionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Guard.NotNull(endpoints);
        var group = endpoints.MapGroup("access")
            .BelongsTo<AccessModule>()
            .LoadsNoResource()
            .AddErrorTranslation();

        group.MapPost("/users/decide", async (UserDecisionRequest request, IAccessDecision decision, CancellationToken ct) =>
            {
                if (request is null || !Compartment.TryParse(request.Compartment, out var compartment))
                    return Invalid("A decision for a person names the compartment.");
                if (Question(request.Subject, request.Operation, request.Via, SubjectKind.Person) is not { } question)
                    return Invalid("A decision names a subject and an operation identity.");
                return (await decision.DecideAsync(question.Subject, question.Via, compartment, question.Operation, request.SecondFactor, ct)).ToHttpResult();
            })
            .WithMetadata(AccessOperations.DecisionsRead)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/admins/decide", async (AdminDecisionRequest request, IAccessDecision decision, CancellationToken ct) =>
            {
                if (request is null || Question(request.Subject, request.Operation, request.Via, SubjectKind.Person) is not { } question)
                    return Invalid("A decision names a subject and an operation identity.");
                return (await decision.DecideAsync(question.Subject, question.Via, Space.Shared, question.Operation, request.SecondFactor, ct)).ToHttpResult();
            })
            .WithMetadata(AccessOperations.DecisionsRead)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/clients/decide", async (ClientDecisionRequest request, IAccessDecision decision, CancellationToken ct) =>
            {
                if (request is null || !SpaceSegments.TryParse(request.Space, out var space))
                    return Invalid("A decision for a client names the space: a compartment, or _shared.");
                if (Question(request.Subject, request.Operation, request.Via, SubjectKind.Client) is not { } question)
                    return Invalid("A decision names a subject and an operation identity.");
                return (await decision.DecideAsync(question.Subject, question.Via, space, question.Operation, secondFactor: false, ct)).ToHttpResult();
            })
            .WithMetadata(AccessOperations.DecisionsRead)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return group;
    }

    private static IResult Invalid(string message) => Result.Failure(Error.Validation("Decision.Invalid", message)).ToHttpResult();

    private static (AccessSubject Subject, AccessSubject? Via, Operation Operation)? Question(string? subject, string? operation, string? via, SubjectKind kind)
    {
        if (string.IsNullOrWhiteSpace(subject) || !Actor.TryParse(subject, out _) || !OperationIdentities.IsIdentity(operation))
            return null;
        if (via is not null && (string.IsNullOrWhiteSpace(via) || !Actor.TryParse(via, out _)))
            return null;
        var dot = operation.IndexOf('.', StringComparison.Ordinal);
        return (new AccessSubject(kind, subject), via is null ? null : AccessSubject.Client(via), new Operation(operation[..dot], operation[(dot + 1)..], OperationKind.Read));
    }
}
