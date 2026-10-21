using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Access.Client;

/// <summary>
/// The options of a resource server reaching the host that holds Access, bound from <c>Stellaegis:Access:Client</c>:
/// the address under which the decision routes are mapped.
/// <code>
/// Stellaegis:Access:Client:BaseUrl = https://access.internal/api/access
/// </code>
/// </summary>
public sealed class AccessClientOptions : StellaegisClientOptions
{
    /// <summary>The configuration section of the options, <c>Stellaegis:Access:Client</c>.</summary>
    public const string SectionName = "Stellaegis:Access:Client";
}

/// <summary>
/// Gives the client the bearer token it presents to the decision routes — which Access itself decides, under
/// <c>decisions.read</c> granted to the client of the resource server in the shared space. The host implements it
/// over its own way of obtaining one, the client credentials of <c>Stellaegis.Issuer.Client</c> usually:
/// <code>
/// services.AddSingleton&lt;IAccessClientTokenProvider&gt;(provider => new M2MAccessToken(provider.GetRequiredService&lt;M2MTokenProvider&gt;()));
/// </code>
/// Without one registered, the client presents no token, for a host that authenticates otherwise.
/// </summary>
public interface IAccessClientTokenProvider
{
    /// <summary>The token to present, or <see langword="null"/> for none.</summary>
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The decision asked over HTTP, under the same port as in process: a person in a compartment goes to
/// <c>users/decide</c>, a person in the shared space to <c>admins/decide</c>, a client to <c>clients/decide</c>.
/// A 204 is a yes; a problem details is the refusal, with its code; anything else is an error of transport.
/// </summary>
public sealed class HttpAccessDecision(HttpClient http, IServiceProvider services) : IAccessDecision
{
    private readonly IAccessClientTokenProvider? _tokens = Guard.NotNull(services).GetService<IAccessClientTokenProvider>();

    /// <inheritdoc/>
    public ValueTask<Result> DecideAsync(Actor actor, Space space, Operation operation, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(actor);
        return DecideAsync(AccessSubject.Of(actor), actor.Via is { } via ? AccessSubject.Of(via) : null, space, operation, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<Result> DecideAsync(AccessSubject subject, AccessSubject? via, Space space, Operation operation, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(subject);
        Guard.NotNull(space);
        Guard.NotNull(operation);

        var (route, body) = subject.Kind == SubjectKind.Client
            ? ("clients/decide", (object)new ClientQuestion(subject.Id, space.ToSegment(), operation.Identity, via?.Id))
            : space.Compartment is { } compartment
                ? ("users/decide", new UserQuestion(subject.Id, compartment.Value, operation.Identity, via?.Id))
                : ("admins/decide", new AdminQuestion(subject.Id, operation.Identity, via?.Id));

        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, body.GetType()) };
        if (_tokens is not null && await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false) is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return Result.Success();
        if (await response.ReadErrorAsync(cancellationToken).ConfigureAwait(false) is { } error)
            return Result.Failure(error);
        throw new HttpRequestException($"Access answered {(int)response.StatusCode} without an error to read.", null, response.StatusCode);
    }

    private sealed record UserQuestion(string Subject, string Compartment, string Operation, string? Via);

    private sealed record AdminQuestion(string Subject, string Operation, string? Via);

    private sealed record ClientQuestion(string Subject, string Space, string Operation, string? Via);
}

/// <summary>Registers the decision over HTTP, with its cache.</summary>
public static class AccessClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAccessDecision"/> over HTTP, its options bound from <paramref name="sectionName"/> and
    /// validated at startup, behind the cache of decisions (<see cref="AccessDecisionCache"/>, options under
    /// <c>Stellaegis:Access:Cache</c>). For a resource server that has no Access in process:
    /// <code>
    /// builder.Services.AddStellaegisAccessClient(builder.Configuration).AddStellaegisAccessPolicies();
    /// </code>
    /// Register an <see cref="IAccessClientTokenProvider"/> before when the routes ask a token. The host connects its
    /// subscriptions to the events of Access on <see cref="AccessDecisionCache"/>, so that a changed right is seen
    /// before the entries expire.
    /// </summary>
    /// <returns>The builder of the underlying HTTP client, to add handlers to.</returns>
    public static IHttpClientBuilder AddStellaegisAccessClient(this IServiceCollection services, IConfiguration configuration, string sectionName = AccessClientOptions.SectionName)
    {
        Guard.NotNull(services);
        Guard.NotNull(configuration);
        services.AddStellaegisOptions<AccessCacheOptions>(configuration, AccessCacheOptions.SectionName);
        var builder = services.AddStellaegisHttpClient<HttpAccessDecision, HttpAccessDecision, AccessClientOptions>(configuration, sectionName);
        Cached(services);
        return builder;
    }

    /// <summary>
    /// Registers the decision over HTTP as <see cref="AddStellaegisAccessClient(IServiceCollection, IConfiguration, string)"/>
    /// does, with its options configured in code.
    /// </summary>
    public static IHttpClientBuilder AddStellaegisAccessClient(this IServiceCollection services, Action<AccessClientOptions> configure, Action<AccessCacheOptions>? configureCache = null)
    {
        Guard.NotNull(services);
        Guard.NotNull(configure);
        services.AddStellaegisOptions<AccessCacheOptions>(configureCache ?? (_ => { }));
        var builder = services.AddStellaegisHttpClient<HttpAccessDecision, HttpAccessDecision, AccessClientOptions>(configure);
        Cached(services);
        return builder;
    }

    private static void Cached(IServiceCollection services)
    {
        services.TryAddSingleton<AccessDecisionCache>();
        services.AddScoped<IAccessDecision>(provider => new CachedAccessDecision(
            provider.GetRequiredService<HttpAccessDecision>(),
            provider.GetRequiredService<AccessDecisionCache>()));
    }
}
