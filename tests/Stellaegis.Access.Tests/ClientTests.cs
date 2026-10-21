using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Access.AspNetCore;
using Stellaegis.Access.Client;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;
using Stellaegis.Primitives.Hosting;
using static Stellaegis.Access.Tests.Fixtures;

namespace Stellaegis.Access.Tests;

/// <summary>
/// A resource server apart, whose rights system asks Access over HTTP: the decision under the same port, the cache
/// that keeps a yes, the invalidation that drops it.
/// </summary>
public sealed class ClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The token the inventory presents to Access: its own client_id, which the header authentication reads as such.</summary>
    private sealed class InventoryToken : IAccessClientTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(Inventory);
    }

    /// <summary>The host that holds Access, with the inventory allowed to ask.</summary>
    private static async Task<WebApplication> StartAccessAsync()
    {
        var app = await WebTestHost.StartAsync(ct: Ct);
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAccessProjection>().ReplaceClientGrantsAsync(Inventory, Space.Shared, [AccessOperations.DecisionsRead.Identity], Ct);
        return app;
    }

    /// <summary>
    /// The inventory, a resource server apart: a compartmented module, Access over HTTP reaching the first host
    /// through the test server, the same header authentication for its own callers.
    /// </summary>
    private static async Task<WebApplication> StartInventoryAsync(WebApplication access, bool cache = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddStellaegisFrame(ApplicationFrame.ManyCompartments().WithSharedSpace());
        builder.Services.AddStellaegisRequestContext(c => c.CompartmentFromRoute("org").ActorFrom(WebTestHost.PersonOrClient).ViaFromClaim("act"));
        builder.Services.AddStellaegisModule(ItemsModule.Declaration);
        builder.Services.AddSingleton<IAccessClientTokenProvider, InventoryToken>();
        builder.Services
            .AddStellaegisAccessClient(options => options.BaseUrl = "http://access.test/api/access", cacheOptions => cacheOptions.Enabled = cache)
            .ConfigurePrimaryHttpMessageHandler(() => access.GetTestServer().CreateHandler());
        builder.Services.AddStellaegisAccessPolicies();
        builder.Services.AddAuthentication(WebTestHost.HeaderAuthentication.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, WebTestHost.HeaderAuthentication>(WebTestHost.HeaderAuthentication.SchemeName, null);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var items = app.MapGroup("/api/orgs/{org}").MapGroup("/items").BelongsTo<ItemsModule>().RequireRequestContext().LoadsNoResource();
        items.MapGet("/", (IRequestContext context) => Results.Ok(new WebTestHost.Seen(context.Actor.Value, context.Actor.Via?.Value, context.Space!.ToSegment())))
            .WithMetadata(ItemsRead);
        items.RequireAuthorizationPerOperation(op => op.ToString());
        await app.StartAsync(Ct);
        return app;
    }

    [Fact]
    public async Task A_resource_server_apart_is_decided_by_Access_over_HTTP()
    {
        await using var access = await StartAccessAsync();
        await using var inventory = await StartInventoryAsync(access, cache: false);
        using var scope = access.Services.CreateScope();
        var projection = scope.ServiceProvider.GetRequiredService<IAccessProjection>();

        Assert.Equal(HttpStatusCode.Forbidden, (await inventory.AsPerson(Jane).GetAsync("/api/orgs/acme/items", Ct)).StatusCode);

        await projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        await projection.ReplaceUserGrantsAsync(Jane, Acme, [ItemsRead.Identity], Ct);
        Assert.Equal(new WebTestHost.Seen(Jane, null, "acme"), await inventory.AsPerson(Jane).GetFromJsonAsync<WebTestHost.Seen>("/api/orgs/acme/items", Ct));

        // A client, and a person through a client, take their own routes of the decision.
        await projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
        Assert.Equal(HttpStatusCode.OK, (await inventory.AsClient(Desktop).GetAsync("/api/orgs/acme/items", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await inventory.AsPerson(Jane, via: Desktop).GetAsync("/api/orgs/acme/items", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inventory.AsPerson(Jane, via: Inventory).GetAsync("/api/orgs/acme/items", Ct)).StatusCode);

        // The refusal carries the code of Access, read on the client side.
        var decision = inventory.Services.CreateScope().ServiceProvider.GetRequiredService<IAccessDecision>();
        var refused = await decision.DecideAsync(JaneActor, Globex, ItemsRead, Ct);
        Assert.True(refused.IsFailure);
        Assert.Equal(AccessErrors.NotMemberCode, refused.Error!.Code);
    }

    [Fact]
    public async Task The_cache_keeps_a_yes_until_the_host_invalidates_it()
    {
        await using var access = await StartAccessAsync();
        await using var inventory = await StartInventoryAsync(access);
        using var scope = access.Services.CreateScope();
        var projection = scope.ServiceProvider.GetRequiredService<IAccessProjection>();
        var cache = inventory.Services.GetRequiredService<AccessDecisionCache>();
        Assert.True(cache.Enabled);

        await projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        await projection.ReplaceUserGrantsAsync(Jane, Acme, [ItemsRead.Identity], Ct);
        Assert.Equal(HttpStatusCode.OK, (await inventory.AsPerson(Jane).GetAsync("/api/orgs/acme/items", Ct)).StatusCode);
        Assert.True(cache.TryGetYes(AccessSubject.Person(Jane), null, Acme, ItemsRead));

        // The right is withdrawn in Access; the inventory still holds the yes, until it hears of it.
        await projection.ReplaceUserGrantsAsync(Jane, Acme, [], Ct);
        Assert.Equal(HttpStatusCode.OK, (await inventory.AsPerson(Jane).GetAsync("/api/orgs/acme/items", Ct)).StatusCode);

        cache.Invalidate(new GrantsChangedEvent { OccurredAt = DateTimeOffset.UtcNow, Subject = AccessSubject.Person(Jane), Compartment = Acme });
        Assert.False(cache.TryGetYes(AccessSubject.Person(Jane), null, Acme, ItemsRead));
        Assert.Equal(HttpStatusCode.Forbidden, (await inventory.AsPerson(Jane).GetAsync("/api/orgs/acme/items", Ct)).StatusCode);

        // A refusal is never kept: granted again, seen at once.
        await projection.ReplaceUserGrantsAsync(Jane, Acme, [ItemsRead.Identity], Ct);
        Assert.Equal(HttpStatusCode.OK, (await inventory.AsPerson(Jane).GetAsync("/api/orgs/acme/items", Ct)).StatusCode);

        // The compartment removed drops every decision in it; a via is a subject of the entry too.
        cache.SetYes(AccessSubject.Person(Jane), AccessSubject.Client(Desktop), Acme, ItemsRead);
        cache.Invalidate(new SubjectRemovedEvent { OccurredAt = DateTimeOffset.UtcNow, Subject = AccessSubject.Client(Desktop) });
        Assert.False(cache.TryGetYes(AccessSubject.Person(Jane), AccessSubject.Client(Desktop), Acme, ItemsRead));
        Assert.True(cache.TryGetYes(AccessSubject.Person(Jane), null, Acme, ItemsRead));
        cache.Invalidate(new CompartmentRemovedEvent { OccurredAt = DateTimeOffset.UtcNow, Compartment = Acme });
        Assert.False(cache.TryGetYes(AccessSubject.Person(Jane), null, Acme, ItemsRead));
    }

    [Fact]
    public void The_cache_options_are_validated_and_a_disabled_cache_keeps_nothing()
    {
        Assert.Equal("Expiration must be positive.", new AccessCacheOptions { Expiration = TimeSpan.Zero }.FindError());
        Assert.Equal("SizeLimit must be at least 1.", new AccessCacheOptions { SizeLimit = 0 }.FindError());
        Assert.Null(new AccessCacheOptions().FindError());

        using var disabled = new AccessDecisionCache(Options.Create(new AccessCacheOptions { Enabled = false }));
        disabled.SetYes(AccessSubject.Person(Jane), null, Acme, ItemsRead);
        Assert.False(disabled.Enabled);
        Assert.False(disabled.TryGetYes(AccessSubject.Person(Jane), null, Acme, ItemsRead));
    }

    [Fact]
    public async Task A_client_without_a_valid_address_refuses_the_host()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddStellaegisAccessClient(options => options.BaseUrl = "access.test");
        var app = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync(Ct));
    }
}
