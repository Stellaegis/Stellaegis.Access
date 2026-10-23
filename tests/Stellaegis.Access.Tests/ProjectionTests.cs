using Microsoft.Extensions.DependencyInjection;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;
using static Stellaegis.Access.Tests.Fixtures;

namespace Stellaegis.Access.Tests;

/// <summary>Snapshots in, flat rows out: what a projection writes, removes and publishes.</summary>
public sealed class ProjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_snapshot_removes_everything_of_the_subject_in_the_space()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceMembershipsAsync(Jane, [Acme, Globex], Ct);
        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity), new(ItemsWrite.Identity)], Ct);
        Assert.Equal([Acme, Globex], await host.Services.GetRequiredService<IMembershipStore>().ListAsync(Jane, Ct));
        Assert.Equal(["items.read", "items.write"], (await host.Services.GetRequiredService<IUserGrantStore>().ListAsync(Jane, Acme, Ct)).Select(grant => grant.Operation));

        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [], Ct);
        await host.Projection.ReplaceMembershipsAsync(Jane, [], Ct);

        Assert.Empty((await host.Services.GetRequiredService<IUserGrantStore>().ListAsync(Jane, Acme, Ct)).Select(grant => grant.Operation));
        Assert.Empty(await host.Services.GetRequiredService<IMembershipStore>().ListAsync(Jane, Ct));
        Assert.Empty(host.Data.UserGrants);
        Assert.Empty(host.Data.Memberships);
    }

    [Fact]
    public async Task A_snapshot_replaces_rather_than_adds()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceAdminGrantsAsync(Jane, [new(ItemsRead.Identity), new(ItemsWrite.Identity)], Ct);
        await host.Projection.ReplaceAdminGrantsAsync(Jane, [new(UsersRead.Identity)], Ct);

        Assert.Equal(["users.read"], (await host.Services.GetRequiredService<IAdminGrantStore>().ListAsync(Jane, Ct)).Select(grant => grant.Operation));
        Assert.Null(await host.Services.GetRequiredService<IAdminGrantStore>().FindAsync(Jane, ItemsRead.Identity, Ct));
    }

    [Fact]
    public async Task A_removed_compartment_takes_its_memberships_and_rights_with_it()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceMembershipsAsync(Jane, [Acme, Globex], Ct);
        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity)], Ct);
        await host.Projection.ReplaceUserGrantsAsync(Jane, Globex, [new(ItemsRead.Identity)], Ct);
        await host.Projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
        await host.Projection.ReplaceClientGrantsAsync(Inventory, Space.Shared, [UsersRead.Identity], Ct);

        await host.Projection.RemoveCompartmentAsync(Acme, Ct);

        Assert.Equal([Globex], await host.Services.GetRequiredService<IMembershipStore>().ListAsync(Jane, Ct));
        Assert.Empty((await host.Services.GetRequiredService<IUserGrantStore>().ListAsync(Jane, Acme, Ct)).Select(grant => grant.Operation));
        Assert.Equal(["items.read"], (await host.Services.GetRequiredService<IUserGrantStore>().ListAsync(Jane, Globex, Ct)).Select(grant => grant.Operation));
        Assert.False(await host.Services.GetRequiredService<IClientGrantStore>().HasAnyAsync(Desktop, Acme, Ct));
        Assert.True(await host.Services.GetRequiredService<IClientGrantStore>().HasAnyAsync(Inventory, Space.Shared, Ct));
        Assert.True((await host.Decision.DecideAsync(JaneActor, Globex, ItemsRead, secondFactor: false, Ct)).IsSuccess);
        Assert.True((await host.Decision.DecideAsync(JaneActor, Acme, ItemsRead, secondFactor: false, Ct)).IsFailure);
    }

    [Fact]
    public async Task A_removed_subject_loses_everything_in_every_space()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity)], Ct);
        await host.Projection.ReplaceAdminGrantsAsync(Jane, [new(UsersRead.Identity)], Ct);
        await host.Projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
        await host.Projection.ReplaceClientGrantsAsync(Desktop, Space.Shared, [ItemsRead.Identity], Ct);

        await host.Projection.RemoveSubjectAsync(AccessSubject.Person(Jane), Ct);
        await host.Projection.RemoveSubjectAsync(AccessSubject.Client(Desktop), Ct);

        Assert.Empty(host.Data.Memberships);
        Assert.Empty(host.Data.UserGrants);
        Assert.Empty(host.Data.AdminGrants);
        Assert.Empty(host.Data.ClientGrants);
    }

    [Fact]
    public async Task Every_projection_publishes_its_event()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity)], Ct);
        await host.Projection.ReplaceAdminGrantsAsync(Jane, [new(UsersRead.Identity)], Ct);
        await host.Projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
        await host.Projection.RemoveCompartmentAsync(Globex, Ct);
        await host.Projection.RemoveSubjectAsync(AccessSubject.Client(Desktop), Ct);

        var events = host.Events.Events.ToArray();
        Assert.Equal(6, events.Length);
        Assert.Equal(Jane, Assert.IsType<MembershipsChangedEvent>(events[0]).Subject);
        var userGrants = Assert.IsType<GrantsChangedEvent>(events[1]);
        Assert.Equal(AccessSubject.Person(Jane), userGrants.Subject);
        Assert.Equal(Acme, userGrants.Compartment);
        var adminGrants = Assert.IsType<GrantsChangedEvent>(events[2]);
        Assert.Equal(AccessSubject.Person(Jane), adminGrants.Subject);
        Assert.Null(adminGrants.Compartment);
        var clientGrants = Assert.IsType<GrantsChangedEvent>(events[3]);
        Assert.Equal(AccessSubject.Client(Desktop), clientGrants.Subject);
        Assert.Equal(Acme, clientGrants.Compartment);
        Assert.Equal(Globex, Assert.IsType<CompartmentRemovedEvent>(events[4]).Compartment);
        Assert.Equal(AccessSubject.Client(Desktop), Assert.IsType<SubjectRemovedEvent>(events[5]).Subject);
        Assert.All(events, e => Assert.NotEqual(default, e.OccurredAt));
    }

    [Fact]
    public async Task A_malformed_operation_identity_is_refused_not_stored()
    {
        using var host = new TestHost();

        await Assert.ThrowsAsync<ArgumentException>(() => host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [new("items-read")], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Projection.ReplaceAdminGrantsAsync(Jane, [new("read", true)], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Projection.ReplaceClientGrantsAsync(Desktop, Acme, ["Items.Read"], Ct));
        Assert.Empty(host.Data.UserGrants);
        Assert.Empty(host.Events.Events);

        Assert.True(OperationIdentities.IsIdentity("fiscal_years.validate"));
        Assert.False(OperationIdentities.IsIdentity("fiscal_years"));
        Assert.False(OperationIdentities.IsIdentity(".validate"));
        Assert.False(OperationIdentities.IsIdentity("a.b.c"));
    }
    [Fact]
    public async Task An_operation_projected_twice_requires_a_second_factor_when_either_says_so()
    {
        using var host = new TestHost();

        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity), new(ItemsRead.Identity, RequiresSecondFactor: true), new(ItemsWrite.Identity)], Ct);

        Assert.Equal([new AccessGrant(ItemsRead.Identity, true), new AccessGrant(ItemsWrite.Identity, false)],
            await host.Services.GetRequiredService<IUserGrantStore>().ListAsync(Jane, Acme, Ct));
    }
}

/// <summary>What the module declares, and how it is registered.</summary>
public sealed class ModuleTests
{
    [Fact]
    public void The_module_has_no_compartments_and_fits_every_frame()
    {
        ApplicationFrame.WithoutCompartments.Validate([AccessModule.Declaration]);
        ApplicationFrame.SingleCompartment("acme").Validate([AccessModule.Declaration]);
        ApplicationFrame.ManyCompartments().WithSharedSpace().Validate([AccessModule.Declaration]);

        Assert.Equal("access", AccessModule.Name);
        Assert.Equal(ModuleCompartments.None, AccessModule.Declaration.Compartments);
        Assert.False(AccessModule.Declaration.AcceptsSharedSpace);
        Assert.Equal(["decisions.read", "grants.read", "grants.write"], AccessModule.Declaration.Operations.Select(o => o.Identity));
        Assert.Equal(AccessModule.Declaration.Operations, AccessOperations.All);
    }

    [Fact]
    public void The_registration_is_idempotent_and_resolves_the_decision_and_the_projection()
    {
        var services = new ServiceCollection().AddLogging().AddStellaegisAccessInMemory().AddStellaegisAccessInMemory().AddStellaegisAccess();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        Assert.Single(provider.GetStellaegisModules());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAccessDecision>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAccessProjection>());
        Assert.IsType<NullEventPublisher>(provider.GetRequiredService<IEventPublisher>());
    }

}
