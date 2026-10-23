using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Stellaegis.Access.AspNetCore;
using Stellaegis.Primitives;
using static Stellaegis.Access.Tests.Fixtures;

namespace Stellaegis.Access.Tests;

/// <summary>The decision and projection routes, under the operations Access itself decides.</summary>
public sealed class EndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Registry = "registry";

    /// <summary>A host where the inventory may ask decisions and the registry may project, both in the shared space.</summary>
    private static async Task<WebApplication> StartAsync()
    {
        var app = await WebTestHost.StartAsync(ct: Ct);
        using var scope = app.Services.CreateScope();
        var projection = scope.ServiceProvider.GetRequiredService<IAccessProjection>();
        await projection.ReplaceClientGrantsAsync(Inventory, Space.Shared, [AccessOperations.DecisionsRead.Identity], Ct);
        await projection.ReplaceClientGrantsAsync(Registry, Space.Shared, [AccessOperations.GrantsWrite.Identity, AccessOperations.GrantsRead.Identity], Ct);
        return app;
    }

    [Fact]
    public async Task The_registry_projects_and_the_inventory_asks_the_three_decisions()
    {
        await using var app = await StartAsync();
        var registry = app.AsClient(Registry);
        var inventory = app.AsClient(Inventory);

        // Projected over HTTP, read back over HTTP.
        Assert.Equal(HttpStatusCode.NoContent, (await registry.PutAsJsonAsync($"/api/access/memberships/{Jane}", new[] { "acme" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await registry.PutAsJsonAsync($"/api/access/user-grants/{Jane}/acme", new[] { new AccessGrant(ItemsRead.Identity), new AccessGrant(PayrollPay.Identity, true) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await registry.PutAsJsonAsync($"/api/access/admin-grants/{Jane}", new[] { new AccessGrant(UsersRead.Identity) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await registry.PutAsJsonAsync($"/api/access/client-grants/{Desktop}/acme", new[] { ItemsRead.Identity }, Ct)).StatusCode);
        Assert.Equal(["acme"], (await registry.GetFromJsonAsync<string[]>($"/api/access/memberships/{Jane}", Ct))!);
        Assert.Equal([new AccessGrant(ItemsRead.Identity), new AccessGrant(PayrollPay.Identity, true)], (await registry.GetFromJsonAsync<AccessGrant[]>($"/api/access/user-grants/{Jane}/acme", Ct))!);
        Assert.Equal([new AccessGrant(UsersRead.Identity)], (await registry.GetFromJsonAsync<AccessGrant[]>($"/api/access/admin-grants/{Jane}", Ct))!);
        Assert.Equal([ItemsRead.Identity], (await registry.GetFromJsonAsync<string[]>($"/api/access/client-grants/{Desktop}/acme", Ct))!);

        // The three decisions.
        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync("/api/access/users/decide", new UserDecisionRequest(Jane, "acme", ItemsRead.Identity), Ct)).StatusCode);
        var denied = await inventory.PostAsJsonAsync("/api/access/users/decide", new UserDecisionRequest(Jane, "acme", ItemsWrite.Identity), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(AccessErrors.DeniedCode, await denied.CodeOfAsync(Ct));
        var notMember = await inventory.PostAsJsonAsync("/api/access/users/decide", new UserDecisionRequest(Jane, "globex", ItemsRead.Identity), Ct);
        Assert.Equal(AccessErrors.NotMemberCode, await notMember.CodeOfAsync(Ct));
        var viaDenied = await inventory.PostAsJsonAsync("/api/access/users/decide", new UserDecisionRequest(Jane, "acme", ItemsRead.Identity, Via: Inventory), Ct);
        Assert.Equal(AccessErrors.ViaDeniedCode, await viaDenied.CodeOfAsync(Ct));
        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync("/api/access/users/decide", new UserDecisionRequest(Jane, "acme", ItemsRead.Identity, Via: Desktop), Ct)).StatusCode);
        var withoutSecondFactor = await inventory.PostAsJsonAsync("/api/access/users/decide", new UserDecisionRequest(Jane, "acme", PayrollPay.Identity), Ct);
        Assert.Equal(AccessErrors.SecondFactorRequiredCode, await withoutSecondFactor.CodeOfAsync(Ct));
        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync("/api/access/users/decide", new UserDecisionRequest(Jane, "acme", PayrollPay.Identity, SecondFactor: true), Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync("/api/access/admins/decide", new AdminDecisionRequest(Jane, UsersRead.Identity), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inventory.PostAsJsonAsync("/api/access/admins/decide", new AdminDecisionRequest(Jane, ItemsRead.Identity), Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync("/api/access/clients/decide", new ClientDecisionRequest(Desktop, "acme", ItemsRead.Identity), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inventory.PostAsJsonAsync("/api/access/clients/decide", new ClientDecisionRequest(Desktop, "_shared", ItemsRead.Identity), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync("/api/access/clients/decide", new ClientDecisionRequest(Inventory, "_shared", AccessOperations.DecisionsRead.Identity), Ct)).StatusCode);

        // Removals.
        Assert.Equal(HttpStatusCode.NoContent, (await registry.DeleteAsync("/api/access/compartments/acme", Ct)).StatusCode);
        Assert.Empty((await registry.GetFromJsonAsync<string[]>($"/api/access/memberships/{Jane}", Ct))!);
        Assert.Equal(HttpStatusCode.NoContent, (await registry.DeleteAsync($"/api/access/subjects/persons/{Jane}", Ct)).StatusCode);
        Assert.Empty((await registry.GetFromJsonAsync<AccessGrant[]>($"/api/access/admin-grants/{Jane}", Ct))!);
        Assert.Equal(HttpStatusCode.NoContent, (await registry.DeleteAsync($"/api/access/subjects/clients/{Desktop}", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_routes_are_decided_by_Access_itself()
    {
        await using var app = await StartAsync();

        // The inventory asks decisions and projects nothing; the registry projects and asks nothing; a stranger does nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.AsClient(Inventory).PutAsJsonAsync($"/api/access/admin-grants/{Jane}", new[] { new AccessGrant(UsersRead.Identity) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.AsClient(Registry).PostAsJsonAsync("/api/access/admins/decide", new AdminDecisionRequest(Jane, UsersRead.Identity), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.AsClient(Desktop).PostAsJsonAsync("/api/access/admins/decide", new AdminDecisionRequest(Jane, UsersRead.Identity), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetTestClient().PostAsJsonAsync("/api/access/admins/decide", new AdminDecisionRequest(Jane, UsersRead.Identity), Ct)).StatusCode);
    }

    [Fact]
    public async Task An_ill_formed_question_or_snapshot_answers_400_with_its_code()
    {
        await using var app = await StartAsync();
        var registry = app.AsClient(Registry);
        var inventory = app.AsClient(Inventory);

        var badCompartment = await inventory.PostAsJsonAsync("/api/access/users/decide", new UserDecisionRequest(Jane, "Not-A-Compartment", ItemsRead.Identity), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badCompartment.StatusCode);
        Assert.Equal("Decision.Invalid", await badCompartment.CodeOfAsync(Ct));
        Assert.Equal(HttpStatusCode.BadRequest, (await inventory.PostAsJsonAsync("/api/access/admins/decide", new AdminDecisionRequest(Jane, "Items.Read"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await inventory.PostAsJsonAsync("/api/access/clients/decide", new ClientDecisionRequest(Desktop, "shared space", ItemsRead.Identity), Ct)).StatusCode);

        var badSnapshot = await registry.PutAsJsonAsync($"/api/access/user-grants/{Jane}/acme", new[] { new AccessGrant("items read") }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badSnapshot.StatusCode);
        Assert.Equal("Projection.Invalid", await badSnapshot.CodeOfAsync(Ct));
        // The rights of a person are rights, no longer bare operations: the form of 5.x is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await registry.PutAsJsonAsync($"/api/access/admin-grants/{Jane}", new[] { UsersRead.Identity }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await registry.PutAsJsonAsync($"/api/access/memberships/{Jane}", new[] { "Acme" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await registry.DeleteAsync("/api/access/compartments/Acme", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_projection_over_HTTP_publishes_the_event_of_the_module()
    {
        var events = new CapturingEventPublisher();
        await using var app = await WebTestHost.StartAsync(configure: builder => builder.Services.AddSingleton<IEventPublisher>(events), ct: Ct);
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAccessProjection>().ReplaceClientGrantsAsync(Registry, Space.Shared, [AccessOperations.GrantsWrite.Identity], Ct);
        events.Events.Clear();

        Assert.Equal(HttpStatusCode.NoContent, (await app.AsClient(Registry).PutAsJsonAsync($"/api/access/user-grants/{Jane}/acme", new[] { new AccessGrant(ItemsRead.Identity) }, Ct)).StatusCode);

        var changed = Assert.IsType<GrantsChangedEvent>(Assert.Single(events.Events));
        Assert.Equal(AccessSubject.Person(Jane), changed.Subject);
        Assert.Equal(Acme, changed.Compartment);
    }
}
