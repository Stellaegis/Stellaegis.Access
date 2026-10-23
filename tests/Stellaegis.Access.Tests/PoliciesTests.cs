using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Stellaegis.Access.AspNetCore;
using Stellaegis.Primitives;
using static Stellaegis.Access.Tests.Fixtures;

namespace Stellaegis.Access.Tests;

/// <summary>Access as the rights system of a host: the policy per operation, answered from the request.</summary>
public sealed class PoliciesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IAccessProjection ProjectionOf(WebApplication app, out IServiceScope scope)
    {
        scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IAccessProjection>();
    }

    [Fact]
    public async Task A_person_in_a_compartment_is_decided_by_membership_then_right()
    {
        await using var app = await WebTestHost.StartAsync(ct: Ct);
        var projection = ProjectionOf(app, out var scope);
        using (scope)
        {
            var jane = app.AsPerson(Jane);

            Assert.Equal(HttpStatusCode.Forbidden, (await jane.GetAsync("/api/orgs/acme/items", Ct)).StatusCode);

            await projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
            Assert.Equal(HttpStatusCode.Forbidden, (await jane.GetAsync("/api/orgs/acme/items", Ct)).StatusCode);

            await projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity)], Ct);
            var seen = await jane.GetFromJsonAsync<WebTestHost.Seen>("/api/orgs/acme/items", Ct);
            Assert.Equal(new WebTestHost.Seen(Jane, null, "acme"), seen);

            Assert.Equal(HttpStatusCode.Forbidden, (await jane.PostAsync("/api/orgs/acme/items", null, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await jane.GetAsync("/api/orgs/globex/items", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task A_client_is_decided_in_the_space_of_the_request_and_nowhere_else()
    {
        await using var app = await WebTestHost.StartAsync(ct: Ct);
        var projection = ProjectionOf(app, out var scope);
        using (scope)
        {
            await projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
            var desktop = app.AsClient(Desktop);

            var seen = await desktop.GetFromJsonAsync<WebTestHost.Seen>("/api/orgs/acme/items", Ct);
            Assert.Equal(new WebTestHost.Seen(Desktop, null, "acme"), seen);
            Assert.Equal(HttpStatusCode.Forbidden, (await desktop.GetAsync("/api/orgs/globex/items", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await desktop.PostAsync("/api/orgs/acme/items", null, Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task A_person_acting_through_a_client_gets_the_intersection()
    {
        await using var app = await WebTestHost.StartAsync(ct: Ct);
        var projection = ProjectionOf(app, out var scope);
        using (scope)
        {
            await projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
            await projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity), new(ItemsWrite.Identity)], Ct);
            await projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
            var janeViaDesktop = app.AsPerson(Jane, via: Desktop);

            var seen = await janeViaDesktop.GetFromJsonAsync<WebTestHost.Seen>("/api/orgs/acme/items", Ct);
            Assert.Equal(new WebTestHost.Seen(Jane, Desktop, "acme"), seen);
            Assert.Equal(HttpStatusCode.Forbidden, (await janeViaDesktop.PostAsync("/api/orgs/acme/items", null, Ct)).StatusCode);

            // Jane alone writes; through a client that does not hold the write, she does not.
            Assert.Equal(HttpStatusCode.OK, (await app.AsPerson(Jane).PostAsync("/api/orgs/acme/items", null, Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task A_route_without_a_request_context_is_decided_in_the_shared_space()
    {
        await using var app = await WebTestHost.StartAsync(ct: Ct);
        var projection = ProjectionOf(app, out var scope);
        using (scope)
        {
            var jane = app.AsPerson(Jane);
            Assert.Equal(HttpStatusCode.Forbidden, (await jane.GetAsync("/api/tools", Ct)).StatusCode);

            // A right in a compartment opens nothing in the shared space.
            await projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
            await projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ToolsModule.Read.Identity)], Ct);
            Assert.Equal(HttpStatusCode.Forbidden, (await jane.GetAsync("/api/tools", Ct)).StatusCode);

            await projection.ReplaceAdminGrantsAsync(Jane, [new(ToolsModule.Read.Identity)], Ct);
            Assert.Equal(HttpStatusCode.OK, (await jane.GetAsync("/api/tools", Ct)).StatusCode);

            // A client of the platform, with its right in the shared space.
            Assert.Equal(HttpStatusCode.Forbidden, (await app.AsClient(Inventory).GetAsync("/api/tools", Ct)).StatusCode);
            await projection.ReplaceClientGrantsAsync(Inventory, Space.Shared, [ToolsModule.Read.Identity], Ct);
            Assert.Equal(HttpStatusCode.OK, (await app.AsClient(Inventory).GetAsync("/api/tools", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task A_route_acting_on_spaces_is_decided_in_the_shared_space_not_in_the_compartment_it_names()
    {
        await using var app = await WebTestHost.StartAsync(ct: Ct);
        var projection = ProjectionOf(app, out var scope);
        using (scope)
        {
            // A member of acme holding the purge in acme does not purge acme: the purge is an act of administration.
            await projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
            await projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsModule.Purge.Identity)], Ct);
            Assert.Equal(HttpStatusCode.Forbidden, (await app.AsPerson(Jane).DeleteAsync("/api/admin/items/compartments/acme", Ct)).StatusCode);

            await projection.ReplaceAdminGrantsAsync(Jane, [new(ItemsModule.Purge.Identity)], Ct);
            var seen = await (await app.AsPerson(Jane).DeleteAsync("/api/admin/items/compartments/acme", Ct)).Content.ReadFromJsonAsync<WebTestHost.Seen>(Ct);
            Assert.Equal(new WebTestHost.Seen(Jane, null, "acme"), seen);
        }
    }

    [Fact]
    public async Task An_anonymous_request_is_challenged_and_an_unresolved_compartment_is_refused()
    {
        await using var app = await WebTestHost.StartAsync(ct: Ct);
        var projection = ProjectionOf(app, out var scope);
        using (scope)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetTestClient().GetAsync("/api/orgs/acme/items", Ct)).StatusCode);

            await projection.ReplaceAdminGrantsAsync(Jane, [new(ItemsRead.Identity)], Ct);
            Assert.Equal(HttpStatusCode.Forbidden, (await app.AsPerson(Jane).GetAsync("/api/orgs/Not-A-Compartment/items", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task A_policy_that_is_not_an_operation_goes_to_the_default_provider()
    {
        await using var app = await WebTestHost.StartAsync(
            more: app => app.MapGet("/api/elsewhere", () => Results.Ok()).RequireAuthorization("elsewhere"),
            configure: builder => builder.Services.AddAuthorizationBuilder().AddPolicy("elsewhere", policy => policy.RequireClaim("client_id", Inventory)),
            ct: Ct);

        Assert.Equal(HttpStatusCode.OK, (await app.AsClient(Inventory).GetAsync("/api/elsewhere", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.AsClient(Desktop).GetAsync("/api/elsewhere", Ct)).StatusCode);

        var provider = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        Assert.Null(await provider.GetPolicyAsync("nowhere"));
        Assert.NotNull(await provider.GetPolicyAsync("items.read"));
        Assert.Contains((await provider.GetPolicyAsync("items.read"))!.Requirements, requirement => requirement is AccessRequirement { Operation: "items.read" });
    }

    [Fact]
    public async Task A_policy_required_by_hand_is_decided_too()
    {
        await using var app = await WebTestHost.StartAsync(
            more: app => app.MapGet("/api/by-hand", () => Results.Ok()).RequireAuthorization("tools.read"),
            ct: Ct);
        var projection = ProjectionOf(app, out var scope);
        using (scope)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await app.AsPerson(Jane).GetAsync("/api/by-hand", Ct)).StatusCode);
            await projection.ReplaceAdminGrantsAsync(Jane, [new(ToolsModule.Read.Identity)], Ct);
            Assert.Equal(HttpStatusCode.OK, (await app.AsPerson(Jane).GetAsync("/api/by-hand", Ct)).StatusCode);
        }
    }

    [Fact]
    public void The_requirement_refuses_what_is_not_an_operation_identity()
    {
        Assert.Throws<ArgumentException>(() => new AccessRequirement("Items.Read"));
        Assert.Throws<ArgumentException>(() => new AccessRequirement("items"));
        Assert.Equal("items.read", new AccessRequirement("items.read").ToString());
    }

    [Fact]
    public async Task A_right_that_requires_a_second_factor_opens_to_a_session_whose_amr_holds_mfa()
    {
        await using var app = await WebTestHost.StartAsync(ct: Ct);
        var projection = ProjectionOf(app, out var scope);
        using (scope)
        {
            await projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
            await projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity, RequiresSecondFactor: true)], Ct);

            Assert.Equal(HttpStatusCode.Forbidden, (await app.AsPerson(Jane).GetAsync("/api/orgs/acme/items", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await app.AsPerson(Jane, amr: "pwd").GetAsync("/api/orgs/acme/items", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await app.AsPerson(Jane, amr: "pwd,otp").GetAsync("/api/orgs/acme/items", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await app.AsPerson(Jane, amr: "pwd,otp,mfa").GetAsync("/api/orgs/acme/items", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await app.AsPerson(Jane, amr: "pwd,mfa", amrClaim: "http://schemas.microsoft.com/claims/authnmethodsreferences").GetAsync("/api/orgs/acme/items", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task A_refusal_of_a_policy_answers_problem_details_with_the_code_of_Access()
    {
        await using var app = await WebTestHost.StartAsync(ct: Ct);
        var projection = ProjectionOf(app, out var scope);
        using (scope)
        {
            var notMember = await app.AsPerson(Jane).GetAsync("/api/orgs/acme/items", Ct);
            Assert.Equal(HttpStatusCode.Forbidden, notMember.StatusCode);
            Assert.Equal(AccessErrors.NotMemberCode, await notMember.CodeOfAsync(Ct));

            await projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
            Assert.Equal(AccessErrors.DeniedCode, await (await app.AsPerson(Jane).GetAsync("/api/orgs/acme/items", Ct)).CodeOfAsync(Ct));

            await projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity, RequiresSecondFactor: true)], Ct);
            var secondFactor = await app.AsPerson(Jane, amr: "pwd").GetAsync("/api/orgs/acme/items", Ct);
            Assert.Equal(HttpStatusCode.Forbidden, secondFactor.StatusCode);
            Assert.Equal(AccessErrors.SecondFactorRequiredCode, await secondFactor.CodeOfAsync(Ct));

            // Nobody authenticated is still challenged, as ASP.NET Core does.
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetTestClient().GetAsync("/api/orgs/acme/items", Ct)).StatusCode);
        }
    }

    private sealed class HostOutcomes : Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, Microsoft.AspNetCore.Authorization.Policy.PolicyAuthorizationResult authorizeResult)
            => next(context);
    }

    [Fact]
    public void The_refusals_replace_the_answer_of_ASP_NET_Core_and_never_one_the_host_brought()
    {
        static Type? Outcomes(IServiceCollection services)
            => services.Last(descriptor => descriptor.ServiceType == typeof(Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler)).ImplementationType;

        Assert.Equal("AccessRefusalResultHandler", Outcomes(new ServiceCollection().AddStellaegisAccessPolicies())!.Name);

        var host = new ServiceCollection();
        host.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, HostOutcomes>();
        Assert.Equal(typeof(HostOutcomes), Outcomes(host.AddStellaegisAccessPolicies()));
    }
}
