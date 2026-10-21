using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Access.AspNetCore;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Access.Tests;

/// <summary>A compartmented module of the host under test, its routes read in a compartment of the route.</summary>
internal sealed class ItemsModule : IStellaegisModule
{
    public static string Name => "items";

    public static readonly Operation Purge = new("items", "purge", OperationKind.Delete);

    public static ModuleDeclaration Declaration { get; } = ModuleDeclaration.Compartmented<ItemsModule>()
        .WithOperations(Fixtures.ItemsRead, Fixtures.ItemsWrite, Purge);
}

/// <summary>A module without compartments of the host under test, its routes decided in the shared space.</summary>
internal sealed class ToolsModule : IStellaegisModule
{
    public static string Name => "tools";

    public static readonly Operation Read = new("tools", "read", OperationKind.Read);

    public static ModuleDeclaration Declaration { get; } = ModuleDeclaration.WithoutCompartments<ToolsModule>().WithOperations(Read);
}

/// <summary>
/// A web host with Access in memory as its rights system, on the test server, with an authentication read from
/// headers: <c>X-Person</c> for the <c>sub</c> of a person, <c>X-Client</c> for the <c>client_id</c> of a client,
/// <c>X-Via</c> for the <c>act</c> claim, or <c>Authorization: Bearer {client_id}</c> for a service calling. Its
/// frame has many compartments, read from the route value <c>org</c>, and the shared space.
/// </summary>
internal static class WebTestHost
{
    public static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddStellaegisFrame(ApplicationFrame.ManyCompartments().WithSharedSpace());
        builder.Services.AddStellaegisRequestContext(c => c.CompartmentFromRoute("org").ActorFrom(PersonOrClient).ViaFromClaim("act"));
        builder.Services.AddStellaegisModule(ItemsModule.Declaration);
        builder.Services.AddStellaegisModule(ToolsModule.Declaration);
        builder.Services.AddStellaegisAccessInMemory();
        builder.Services.AddStellaegisAccessPolicies();
        builder.Services.AddAuthentication(HeaderAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>(HeaderAuthentication.SchemeName, null);
        return builder;
    }

    /// <summary>
    /// The actor of a request: a person by <c>sub</c>, or a client by <c>client_id</c>. The default source of
    /// Primitives reads <c>sub</c> and <c>NameIdentifier</c> only: a host that serves clients on compartmented routes
    /// declares a source that reads <c>client_id</c> too, as this one does.
    /// </summary>
    public static Actor? PersonOrClient(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true)
            return null;
        var value = http.User.FindFirst("sub")?.Value ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? http.User.FindFirst("client_id")?.Value;
        return value is null ? null : Actor.Parse(value);
    }

    /// <summary>The host started with the routes of the two test modules and those of Access, one policy per operation.</summary>
    public static async Task<WebApplication> StartAsync(Action<WebApplication>? more = null, Action<WebApplicationBuilder>? configure = null, CancellationToken ct = default)
    {
        var builder = Builder();
        configure?.Invoke(builder);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        var items = app.MapGroup("/api/orgs/{org}").MapGroup("/items").BelongsTo<ItemsModule>().RequireRequestContext().LoadsNoResource();
        items.MapGet("/", (IRequestContext context) => Results.Ok(new Seen(context.Actor.Value, context.Actor.Via?.Value, context.Space!.ToSegment())))
            .WithMetadata(Fixtures.ItemsRead);
        items.MapPost("/", (IRequestContext context) => Results.Ok(new Seen(context.Actor.Value, context.Actor.Via?.Value, context.Space!.ToSegment())))
            .WithMetadata(Fixtures.ItemsWrite);

        var admin = app.MapGroup("/api/admin/items").BelongsTo<ItemsModule>().RequireRequestContext().ActsOnSpaces().LoadsNoResource();
        admin.MapDelete("/compartments/{compartment}", (Compartment compartment, IRequestContext context) => Results.Ok(new Seen(context.Actor.Value, null, compartment.Value)))
            .WithMetadata(ItemsModule.Purge);

        var tools = app.MapGroup("/api/tools").BelongsTo<ToolsModule>().LoadsNoResource();
        tools.MapGet("/", () => Results.Ok()).WithMetadata(ToolsModule.Read);

        app.MapGroup("/api").MapAccessDecisionEndpoints().RequireAuthorizationPerOperation(op => op.ToString());
        app.MapGroup("/api").MapAccessProjectionEndpoints().RequireAuthorizationPerOperation(op => op.ToString());
        foreach (var group in new[] { items, admin, tools })
            group.RequireAuthorizationPerOperation(op => op.ToString());

        more?.Invoke(app);
        await app.StartAsync(ct);
        return app;
    }

    public static HttpClient AsPerson(this WebApplication app, string subject, string? via = null)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(HeaderAuthentication.PersonHeader, subject);
        if (via is not null)
            client.DefaultRequestHeaders.Add(HeaderAuthentication.ViaHeader, via);
        return client;
    }

    public static HttpClient AsClient(this WebApplication app, string clientId)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(HeaderAuthentication.ClientHeader, clientId);
        return client;
    }

    public static async Task<string?> CodeOfAsync(this HttpResponseMessage response, CancellationToken ct)
        => (await response.Content.ReadFromJsonAsync<ProblemBody>(ct))?.Code;

    public sealed record Seen(string Actor, string? Via, string Space);

    private sealed record ProblemBody(string? Code);

    internal sealed class HeaderAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Header";
        public const string PersonHeader = "X-Person";
        public const string ClientHeader = "X-Client";
        public const string ViaHeader = "X-Via";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>();
            if (Request.Headers.TryGetValue(PersonHeader, out var person))
                claims.Add(new Claim("sub", person.ToString()));
            else if (Request.Headers.TryGetValue(ClientHeader, out var client))
                claims.Add(new Claim("client_id", client.ToString()));
            else if (AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var bearer) && bearer.Scheme == "Bearer" && bearer.Parameter is { } token)
                claims.Add(new Claim("client_id", token));
            else
                return Task.FromResult(AuthenticateResult.NoResult());
            if (Request.Headers.TryGetValue(ViaHeader, out var via))
                claims.Add(new Claim("act", via.ToString()));
            var identity = new ClaimsIdentity(claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
