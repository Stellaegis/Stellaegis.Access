using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;

namespace Stellaegis.Access.AspNetCore;

/// <summary>
/// Maps the projection routes of Access, what a registry in another process writes through: snapshots, never
/// deltas. Each write goes through <see cref="IAccessProjection"/>, so that the event of the module is published
/// as it is in process; the reads list what a subject holds.
/// </summary>
/// <remarks>
/// Under <c>grants.write</c>:
/// <list type="bullet">
///   <item><c>PUT access/memberships/{subject}</c> — the compartments the person is admitted in, as an array; empty removes them all</item>
///   <item><c>PUT access/user-grants/{subject}/{compartment}</c> — the operations the person holds in the compartment</item>
///   <item><c>PUT access/admin-grants/{subject}</c> — the operations the person holds in the shared space</item>
///   <item><c>PUT access/client-grants/{subject}/{space}</c> — the operations the client holds in the space, <c>_shared</c> or a compartment</item>
///   <item><c>DELETE access/compartments/{compartment}</c> — the compartment is terminated</item>
///   <item><c>DELETE access/subjects/persons/{subject}</c>, <c>DELETE access/subjects/clients/{subject}</c> — the subject is gone</item>
/// </list>
/// Under <c>grants.read</c>, the <c>GET</c> of the four first routes. The module has no compartments: the routes
/// read no request context, and the host grants these operations, in the shared space, to the client of its
/// registry.
/// </remarks>
public static class AccessProjectionEndpoints
{
    /// <summary>Maps the projection routes under <c>access</c>, relative to <paramref name="endpoints"/>.</summary>
    /// <returns>The group holding the routes, for the host's authorization and further conventions.</returns>
    public static RouteGroupBuilder MapAccessProjectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Guard.NotNull(endpoints);
        var group = endpoints.MapGroup("access")
            .BelongsTo<AccessModule>()
            .LoadsNoResource()
            .AddErrorTranslation();

        group.MapPut("/memberships/{subject}", async (string subject, string[] compartments, IAccessProjection projection, CancellationToken ct) =>
            {
                if (!IsSubject(subject) || compartments is null)
                    return Invalid("A membership snapshot names a person and an array of compartments.");
                var parsed = new List<Compartment>(compartments.Length);
                foreach (var value in compartments)
                {
                    if (!Compartment.TryParse(value, out var compartment))
                        return Invalid($"'{value}' is not a compartment.");
                    parsed.Add(compartment);
                }
                await projection.ReplaceMembershipsAsync(subject, parsed, ct);
                return Results.NoContent();
            })
            .WithMetadata(AccessOperations.GrantsWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/memberships/{subject}", async (string subject, IMembershipStore memberships, CancellationToken ct) =>
                IsSubject(subject)
                    ? Results.Ok((await memberships.ListAsync(subject, ct)).Select(compartment => compartment.Value).ToArray())
                    : Invalid("A subject is an actor."))
            .WithMetadata(AccessOperations.GrantsRead)
            .Produces<string[]>(StatusCodes.Status200OK);

        group.MapPut("/user-grants/{subject}/{compartment}", async (string subject, string compartment, string[] operations, IAccessProjection projection, CancellationToken ct) =>
            {
                if (!IsSubject(subject) || !Compartment.TryParse(compartment, out var parsed))
                    return Invalid("A grant snapshot names a person and a compartment.");
                if (Operations(operations) is not { } identities)
                    return Invalid($"A grant snapshot is an array of operation identities: {OperationIdentities.Rule}.");
                await projection.ReplaceUserGrantsAsync(subject, parsed, identities, ct);
                return Results.NoContent();
            })
            .WithMetadata(AccessOperations.GrantsWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/user-grants/{subject}/{compartment}", async (string subject, string compartment, IUserGrantStore grants, CancellationToken ct) =>
                IsSubject(subject) && Compartment.TryParse(compartment, out var parsed)
                    ? Results.Ok((await grants.ListAsync(subject, parsed, ct)).ToArray())
                    : Invalid("A grant snapshot names a person and a compartment."))
            .WithMetadata(AccessOperations.GrantsRead)
            .Produces<string[]>(StatusCodes.Status200OK);

        group.MapPut("/admin-grants/{subject}", async (string subject, string[] operations, IAccessProjection projection, CancellationToken ct) =>
            {
                if (!IsSubject(subject))
                    return Invalid("A grant snapshot names a person.");
                if (Operations(operations) is not { } identities)
                    return Invalid($"A grant snapshot is an array of operation identities: {OperationIdentities.Rule}.");
                await projection.ReplaceAdminGrantsAsync(subject, identities, ct);
                return Results.NoContent();
            })
            .WithMetadata(AccessOperations.GrantsWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/admin-grants/{subject}", async (string subject, IAdminGrantStore grants, CancellationToken ct) =>
                IsSubject(subject)
                    ? Results.Ok((await grants.ListAsync(subject, ct)).ToArray())
                    : Invalid("A subject is an actor."))
            .WithMetadata(AccessOperations.GrantsRead)
            .Produces<string[]>(StatusCodes.Status200OK);

        group.MapPut("/client-grants/{subject}/{space}", async (string subject, string space, string[] operations, IAccessProjection projection, CancellationToken ct) =>
            {
                if (!IsSubject(subject) || !SpaceSegments.TryParse(space, out var parsed))
                    return Invalid("A grant snapshot names a client and a space: a compartment, or _shared.");
                if (Operations(operations) is not { } identities)
                    return Invalid($"A grant snapshot is an array of operation identities: {OperationIdentities.Rule}.");
                await projection.ReplaceClientGrantsAsync(subject, parsed, identities, ct);
                return Results.NoContent();
            })
            .WithMetadata(AccessOperations.GrantsWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/client-grants/{subject}/{space}", async (string subject, string space, IClientGrantStore grants, CancellationToken ct) =>
                IsSubject(subject) && SpaceSegments.TryParse(space, out var parsed)
                    ? Results.Ok((await grants.ListAsync(subject, parsed, ct)).ToArray())
                    : Invalid("A grant snapshot names a client and a space: a compartment, or _shared."))
            .WithMetadata(AccessOperations.GrantsRead)
            .Produces<string[]>(StatusCodes.Status200OK);

        group.MapDelete("/compartments/{compartment}", async (string compartment, IAccessProjection projection, CancellationToken ct) =>
            {
                if (!Compartment.TryParse(compartment, out var parsed))
                    return Invalid($"'{compartment}' is not a compartment.");
                await projection.RemoveCompartmentAsync(parsed, ct);
                return Results.NoContent();
            })
            .WithMetadata(AccessOperations.GrantsWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapDelete("/subjects/persons/{subject}", async (string subject, IAccessProjection projection, CancellationToken ct) =>
            {
                if (!IsSubject(subject))
                    return Invalid("A subject is an actor.");
                await projection.RemoveSubjectAsync(AccessSubject.Person(subject), ct);
                return Results.NoContent();
            })
            .WithMetadata(AccessOperations.GrantsWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapDelete("/subjects/clients/{subject}", async (string subject, IAccessProjection projection, CancellationToken ct) =>
            {
                if (!IsSubject(subject))
                    return Invalid("A subject is an actor.");
                await projection.RemoveSubjectAsync(AccessSubject.Client(subject), ct);
                return Results.NoContent();
            })
            .WithMetadata(AccessOperations.GrantsWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return group;
    }

    private static IResult Invalid(string message) => Result.Failure(Error.Validation("Projection.Invalid", message)).ToHttpResult();

    private static bool IsSubject(string? subject) => Actor.TryParse(subject, out _);

    private static IReadOnlyCollection<string>? Operations(string[]? operations)
        => operations is not null && operations.All(OperationIdentities.IsIdentity) ? operations : null;
}
