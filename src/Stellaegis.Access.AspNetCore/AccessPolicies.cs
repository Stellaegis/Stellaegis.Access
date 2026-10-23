using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;

namespace Stellaegis.Access.AspNetCore;

/// <summary>
/// The requirement behind a policy named after an operation: that Access decides yes for the actor of the request,
/// in the space of the request, for this operation. One policy per operation, which
/// <c>RequireAuthorizationPerOperation(op => op.ToString())</c> of Primitives names on every labelled route.
/// </summary>
public sealed class AccessRequirement : IAuthorizationRequirement
{
    /// <summary>Wraps the identity of an operation, <c>target.name</c>, as a requirement.</summary>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is not an operation identity.</exception>
    public AccessRequirement(string operation)
        => Operation = Guard.Satisfies(operation, OperationIdentities.IsIdentity, $"'{operation}' is not an operation identity: {OperationIdentities.Rule}.");

    /// <summary>The identity of the operation, <c>target.name</c>.</summary>
    public string Operation { get; }

    /// <inheritdoc/>
    public override string ToString() => Operation;
}

/// <summary>
/// The policy provider of a host whose rights are Access: a policy whose name is an operation identity —
/// <c>documents.read</c>, <c>users.write</c> — requires an authenticated user and the decision of Access; any other
/// name is left to ASP.NET Core's default provider, so the host's own policies keep working beside.
/// </summary>
internal sealed class AccessPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _default = new(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _default.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _default.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!OperationIdentities.IsIdentity(policyName))
            return _default.GetPolicyAsync(policyName);

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new AccessRequirement(policyName))
            .Build();
        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

/// <summary>
/// Answers an <see cref="AccessRequirement"/>: reads the actor, the client acting on its behalf and the space from
/// the request, and asks <see cref="IAccessDecision"/> — the decision in process, or the one over HTTP that
/// <c>Stellaegis.Access.Client</c> registers under the same port. A refusal fails the requirement with the code of
/// the error in its reason, for the host's log; ASP.NET Core answers 403.
/// </summary>
/// <remarks>
/// <para>
/// <b>The space.</b> A route that reads the request context (<c>RequireRequestContext()</c>) is decided in
/// <see cref="IRequestContext.Space"/>: a compartment, or the shared space when the route is marked for it. A route
/// of a module without compartments reads no context, and is decided in the shared space: the accounts, the
/// issuer, Access itself are administered from the platform. A route that acts <b>on</b> spaces
/// (<c>ActsOnSpaces()</c>) is decided in the shared space too: the compartment it names is the object of the
/// route, not the space of the decision — purging a compartment is an act of administration, granted to the
/// operators of the platform, never to a member of the compartment purged.
/// </para>
/// <para>
/// <b>The actor.</b> From the request context when the route reads one, which lets the host choose the claim;
/// from the principal otherwise: <c>client_id</c> makes a client, <c>sub</c> then <c>NameIdentifier</c> a person.
/// The kind is named rather than guessed from the form of the value, since the principal says it. The client
/// acting on the actor's behalf is the <see cref="Actor.Via"/> of the context, or the <c>act</c> claim of a token
/// exchange when the context has none.
/// </para>
/// <para>
/// <b>The second factor.</b> The session proved one when the <c>amr</c> of the principal holds <c>mfa</c> (RFC 8176)
/// — under its name, or the one ASP.NET Core maps it to when it maps inbound claims. A right that requires one is
/// refused without it, with <see cref="AccessErrors.SecondFactorRequired"/>.
/// </para>
/// </remarks>
internal sealed class AccessRequirementHandler(
    IAccessDecision decision,
    IHttpContextAccessor accessor,
    ILogger<AccessRequirementHandler> logger) : AuthorizationHandler<AccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AccessRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return;

        var http = context.Resource as HttpContext ?? accessor.HttpContext;
        if (http is null)
        {
            logger.LogWarning("Access cannot decide {Operation} outside an HTTP request: the requirement is left unmet.", requirement.Operation);
            return;
        }

        var request = Callers.Read(http);
        if (request is null)
            return;

        var operation = http.GetOperation() is { } labelled && labelled.Identity == requirement.Operation
            ? labelled
            : OperationOf(requirement.Operation);
        var result = await decision.DecideAsync(request.Subject, request.Via, request.Space, operation, request.SecondFactor, http.RequestAborted).ConfigureAwait(false);
        if (result.IsSuccess)
            context.Succeed(requirement);
        else
            context.Fail(new AuthorizationFailureReason(this, $"{result.Error!.Code}: {result.Error.Message}"));
    }

    // A policy required by hand, [Authorize(Policy = "items.read")], names an identity without a kind: the kind
    // plays no part in the decision, which reads identities.
    private static Operation OperationOf(string identity)
    {
        var dot = identity.IndexOf('.', StringComparison.Ordinal);
        return new Operation(identity[..dot], identity[(dot + 1)..], OperationKind.Read);
    }
}

/// <summary>What a decision needs from a request: who, through whom, where, and whether the session proved a second factor.</summary>
internal sealed record DecisionRequest(AccessSubject Subject, AccessSubject? Via, Space Space, bool SecondFactor);

/// <summary>Reads the subject, the client acting on its behalf and the space of a request.</summary>
internal static class Callers
{
    private const string ActClaim = "act";

    // amr as the token names it, or as ASP.NET Core maps it when it maps inbound claims; mfa says a second factor (RFC 8176).
    private static readonly string[] MethodClaims = ["amr", "http://schemas.microsoft.com/claims/authnmethodsreferences"];
    private const string MultipleFactors = "mfa";

    /// <summary>
    /// The decision request of an authenticated principal, or <see langword="null"/> when the compartment of the
    /// request cannot be resolved — the endpoint would answer 404; the decision is left unmet, which refuses.
    /// </summary>
    public static DecisionRequest? Read(HttpContext http)
    {
        var endpoint = http.GetEndpoint();
        var readsContext = endpoint?.Metadata.GetMetadata<RequiresRequestContextAttribute>() is not null;
        if (!readsContext)
        {
            var (subject, via) = FromPrincipal(http.User);
            return new DecisionRequest(subject, via, Space.Shared, SecondFactorOf(http.User));
        }

        var context = http.RequestServices.GetRequiredService<IRequestContext>();
        var actor = context.Actor;
        var kind = IsClient(http.User, actor) ? SubjectKind.Client : SubjectKind.Person;
        var actorVia = actor.Via is { } v ? AccessSubject.Client(v.Value) : ViaOf(http.User);
        var space = SpaceOf(context);
        return space is null ? null : new DecisionRequest(new AccessSubject(kind, actor.Value), actorVia, space, SecondFactorOf(http.User));
    }

    /// <summary>Whether the session of the principal proved a second factor: an <c>amr</c> holding <c>mfa</c>.</summary>
    public static bool SecondFactorOf(ClaimsPrincipal user)
        => MethodClaims.SelectMany(user.FindAll).Any(claim => string.Equals(claim.Value, MultipleFactors, StringComparison.Ordinal));

    // On a route acting on spaces, the context's space fails by design (the route reads its object itself): the
    // decision is then taken in the shared space. The metadata that marks such a route is internal to Primitives,
    // so the failure is what tells it.
    private static Space? SpaceOf(IRequestContext context)
    {
        try
        {
            return context.Space;
        }
        catch (InvalidOperationException)
        {
            return Space.Shared;
        }
    }

    private static (AccessSubject Subject, AccessSubject? Via) FromPrincipal(ClaimsPrincipal user)
    {
        if (user.FindFirst("client_id")?.Value is { } clientId && (user.FindFirst("sub") is null && user.FindFirst(ClaimTypes.NameIdentifier) is null))
            return (AccessSubject.Client(clientId), ViaOf(user));
        var subject = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException(
                "The authenticated user carries none of the claims a subject is read from: sub, NameIdentifier, client_id. "
                + "Map the claim the token carries onto one of them.");
        return (AccessSubject.Person(subject), ViaOf(user));
    }

    // A principal is a client when it names a client_id and no person: a token of a client acting for a person
    // carries the person in sub and the client in act.
    private static bool IsClient(ClaimsPrincipal user, Actor actor)
        => user.FindFirst("client_id")?.Value == actor.Value && user.FindFirst("sub") is null && user.FindFirst(ClaimTypes.NameIdentifier) is null;

    // The act claim of a token exchange: the acting party's identifier, or an object naming it in sub.
    private static AccessSubject? ViaOf(ClaimsPrincipal user)
    {
        if (user.FindFirst(ActClaim)?.Value is not { } value)
            return null;
        var id = value.TrimStart().StartsWith('{') ? SubjectOf(value) : value;
        return string.IsNullOrWhiteSpace(id)
            ? throw new InvalidOperationException("The act claim of the authenticated user does not name the client acting on its behalf.")
            : AccessSubject.Client(id);
    }

    private static string? SubjectOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("sub", out var subject)
                && subject.ValueKind == JsonValueKind.String
                    ? subject.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Registers Access as the rights system of a host.</summary>
public static class AccessPoliciesServiceCollectionExtensions
{
    /// <summary>
    /// Makes Access answer every policy named after an operation: the policy provider that recognizes an operation
    /// identity, the handler that reads the request and asks <see cref="IAccessDecision"/>, and the startup check
    /// of Primitives. The decision comes from what the host registered under <see cref="IAccessDecision"/>: Access in
    /// process (<c>AddStellaegisAccessPostgreSQL</c>, <c>AddStellaegisAccessInMemory</c>), or over HTTP
    /// (<c>AddStellaegisAccessClient</c> of <c>Stellaegis.Access.Client</c>).
    /// <code>
    /// builder.Services.AddStellaegisAccessPostgreSQL(builder.Configuration).AddStellaegisAccessPolicies();
    /// ...
    /// app.MapGroup("/api").MapDocumentsEndpoints().RequireAuthorizationPerOperation(op => op.ToString());
    /// </code>
    /// Any policy whose name is not an operation identity keeps going to ASP.NET Core's default provider.
    /// </summary>
    public static IServiceCollection AddStellaegisAccessPolicies(this IServiceCollection services)
    {
        Guard.NotNull(services);
        services.AddAuthorization();
        services.AddHttpContextAccessor();
        services.AddStellaegisStartupCheck();
        services.RemoveAll<IAuthorizationPolicyProvider>();
        services.AddSingleton<IAuthorizationPolicyProvider, AccessPolicyProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, AccessRequirementHandler>());
        return services;
    }
}
