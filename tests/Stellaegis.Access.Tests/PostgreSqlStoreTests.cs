using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Stellaegis.Access.PostgreSQL;
using Stellaegis.Primitives;
using static Stellaegis.Access.Tests.Fixtures;

namespace Stellaegis.Access.Tests;

/// <summary>
/// The decision and the projection against a real PostgreSQL, under the application's role: the three steps
/// replayed on the four tables, the snapshots, the removals, and what the schema itself guarantees.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlStoreTests(PostgreSqlFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceProvider? _provider;
    private IServiceScope? _scope;

    private IServiceProvider Services
    {
        get
        {
            database.SkipWhenUnavailable();
            if (_scope is null)
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IEventPublisher>(Events);
                services.AddStellaegisAccessPostgreSQL(options => options.ConnectionString = database.AppConnectionString);
                _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                _scope = _provider.CreateScope();
            }
            return _scope.ServiceProvider;
        }
    }

    private CapturingEventPublisher Events { get; } = new();

    private IAccessDecision Decision => Services.GetRequiredService<IAccessDecision>();

    private IAccessProjection Projection => Services.GetRequiredService<IAccessProjection>();

    public async ValueTask InitializeAsync()
    {
        if (database.Unavailable is null)
            await database.ClearAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        _scope?.Dispose();
        _provider?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void AssertRefused(Result result, string code)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error!.Code);
    }

    [Fact]
    public async Task A_person_in_a_compartment_needs_a_membership_then_the_right()
    {
        AssertRefused(await Decision.DecideAsync(JaneActor, Acme, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);

        await Projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        AssertRefused(await Decision.DecideAsync(JaneActor, Acme, ItemsRead, secondFactor: false, Ct), AccessErrors.DeniedCode);

        await Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity)], Ct);
        Assert.True((await Decision.DecideAsync(JaneActor, Acme, ItemsRead, secondFactor: false, Ct)).IsSuccess);
        AssertRefused(await Decision.DecideAsync(JaneActor, Acme, ItemsWrite, secondFactor: false, Ct), AccessErrors.DeniedCode);
        AssertRefused(await Decision.DecideAsync(JaneActor, Globex, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);

        Assert.Equal([Acme], await Services.GetRequiredService<IMembershipStore>().ListAsync(Jane, Ct));
        Assert.Equal(["items.read"], (await Services.GetRequiredService<IUserGrantStore>().ListAsync(Jane, Acme, Ct)).Select(grant => grant.Operation));
    }

    [Fact]
    public async Task A_person_in_the_shared_space_is_an_operator_by_any_admin_right()
    {
        AssertRefused(await Decision.DecideAsync(JaneActor, Space.Shared, UsersRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);

        await Projection.ReplaceAdminGrantsAsync(Jane, [new(ItemsRead.Identity)], Ct);
        AssertRefused(await Decision.DecideAsync(JaneActor, Space.Shared, UsersRead, secondFactor: false, Ct), AccessErrors.DeniedCode);

        await Projection.ReplaceAdminGrantsAsync(Jane, [new(ItemsRead.Identity), new(UsersRead.Identity)], Ct);
        Assert.True((await Decision.DecideAsync(JaneActor, Space.Shared, UsersRead, secondFactor: false, Ct)).IsSuccess);
        AssertRefused(await Decision.DecideAsync(JaneActor, Acme, UsersRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
        Assert.Equal(["items.read", "users.read"], (await Services.GetRequiredService<IAdminGrantStore>().ListAsync(Jane, Ct)).Select(grant => grant.Operation));
    }

    [Fact]
    public async Task A_client_is_admitted_in_a_space_by_any_right_there_and_nowhere_else()
    {
        var desktop = Actor.Parse(Desktop);
        var inventory = Actor.Parse(Inventory);

        AssertRefused(await Decision.DecideAsync(desktop, Acme, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);

        await Projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
        await Projection.ReplaceClientGrantsAsync(Inventory, Space.Shared, [UsersRead.Identity], Ct);
        Assert.True((await Decision.DecideAsync(desktop, Acme, ItemsRead, secondFactor: false, Ct)).IsSuccess);
        AssertRefused(await Decision.DecideAsync(desktop, Acme, ItemsWrite, secondFactor: false, Ct), AccessErrors.DeniedCode);
        AssertRefused(await Decision.DecideAsync(desktop, Globex, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
        AssertRefused(await Decision.DecideAsync(desktop, Space.Shared, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
        Assert.True((await Decision.DecideAsync(inventory, Space.Shared, UsersRead, secondFactor: false, Ct)).IsSuccess);
        AssertRefused(await Decision.DecideAsync(inventory, Acme, UsersRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
        Assert.Equal(["users.read"], await Services.GetRequiredService<IClientGrantStore>().ListAsync(Inventory, Space.Shared, Ct));
    }

    [Fact]
    public async Task A_client_acting_for_a_person_needs_the_right_too()
    {
        await Projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        await Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity), new(ItemsWrite.Identity)], Ct);
        var viaDesktop = JaneActor.Through(Actor.Parse(Desktop));

        AssertRefused(await Decision.DecideAsync(viaDesktop, Acme, ItemsRead, secondFactor: false, Ct), AccessErrors.ViaDeniedCode);

        await Projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
        Assert.True((await Decision.DecideAsync(viaDesktop, Acme, ItemsRead, secondFactor: false, Ct)).IsSuccess);
        AssertRefused(await Decision.DecideAsync(viaDesktop, Acme, ItemsWrite, secondFactor: false, Ct), AccessErrors.ViaDeniedCode);
        Assert.True((await Decision.DecideAsync(JaneActor, Acme, ItemsWrite, secondFactor: false, Ct)).IsSuccess);
    }

    [Fact]
    public async Task A_snapshot_replaces_rather_than_adds_and_an_empty_one_removes_everything()
    {
        await Projection.ReplaceMembershipsAsync(Jane, [Acme, Globex], Ct);
        await Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity), new(ItemsWrite.Identity)], Ct);
        await Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(UsersRead.Identity)], Ct);
        Assert.Equal(["users.read"], (await Services.GetRequiredService<IUserGrantStore>().ListAsync(Jane, Acme, Ct)).Select(grant => grant.Operation));
        Assert.Null(await Services.GetRequiredService<IUserGrantStore>().FindAsync(Jane, Acme, ItemsRead.Identity, Ct));

        await Projection.ReplaceUserGrantsAsync(Jane, Acme, [], Ct);
        await Projection.ReplaceMembershipsAsync(Jane, [], Ct);

        Assert.Empty((await Services.GetRequiredService<IUserGrantStore>().ListAsync(Jane, Acme, Ct)).Select(grant => grant.Operation));
        Assert.Empty(await Services.GetRequiredService<IMembershipStore>().ListAsync(Jane, Ct));
        Assert.Equal(0, await CountAsync("authz.memberships"));
        Assert.Equal(0, await CountAsync("authz.user_grants"));
    }

    [Fact]
    public async Task A_snapshot_naming_an_operation_twice_writes_it_once()
    {
        await Projection.ReplaceAdminGrantsAsync(Jane, [new(ItemsRead.Identity), new(ItemsRead.Identity)], Ct);

        Assert.Equal(["items.read"], (await Services.GetRequiredService<IAdminGrantStore>().ListAsync(Jane, Ct)).Select(grant => grant.Operation));
    }

    [Fact]
    public async Task A_removed_compartment_takes_its_memberships_and_rights_with_it()
    {
        await Projection.ReplaceMembershipsAsync(Jane, [Acme, Globex], Ct);
        await Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity)], Ct);
        await Projection.ReplaceUserGrantsAsync(Jane, Globex, [new(ItemsRead.Identity)], Ct);
        await Projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
        await Projection.ReplaceClientGrantsAsync(Inventory, Space.Shared, [UsersRead.Identity], Ct);

        await Projection.RemoveCompartmentAsync(Acme, Ct);

        Assert.Equal([Globex], await Services.GetRequiredService<IMembershipStore>().ListAsync(Jane, Ct));
        Assert.Empty((await Services.GetRequiredService<IUserGrantStore>().ListAsync(Jane, Acme, Ct)).Select(grant => grant.Operation));
        Assert.Equal(["items.read"], (await Services.GetRequiredService<IUserGrantStore>().ListAsync(Jane, Globex, Ct)).Select(grant => grant.Operation));
        Assert.False(await Services.GetRequiredService<IClientGrantStore>().HasAnyAsync(Desktop, Acme, Ct));
        Assert.True(await Services.GetRequiredService<IClientGrantStore>().HasAnyAsync(Inventory, Space.Shared, Ct));
        Assert.True((await Decision.DecideAsync(JaneActor, Globex, ItemsRead, secondFactor: false, Ct)).IsSuccess);
        AssertRefused(await Decision.DecideAsync(JaneActor, Acme, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
    }

    [Fact]
    public async Task A_removed_subject_loses_everything_in_every_space()
    {
        await Projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        await Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity)], Ct);
        await Projection.ReplaceAdminGrantsAsync(Jane, [new(UsersRead.Identity)], Ct);
        await Projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
        await Projection.ReplaceClientGrantsAsync(Desktop, Space.Shared, [ItemsRead.Identity], Ct);

        await Projection.RemoveSubjectAsync(AccessSubject.Person(Jane), Ct);
        await Projection.RemoveSubjectAsync(AccessSubject.Client(Desktop), Ct);

        Assert.Equal(0, await CountAsync("authz.memberships"));
        Assert.Equal(0, await CountAsync("authz.user_grants"));
        Assert.Equal(0, await CountAsync("authz.admin_grants"));
        Assert.Equal(0, await CountAsync("authz.client_grants"));
        Assert.Equal(7, Events.Events.Count);
    }

    [Fact]
    public async Task A_replacement_is_one_transaction_so_that_nothing_half_projected_is_ever_read()
    {
        await Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity)], Ct);

        // A snapshot the schema refuses — a malformed operation reaches the store only past the module's own check,
        // so the test writes through the store — leaves the previous rights in place: the delete rolled back.
        var store = Services.GetRequiredService<IUserGrantStore>();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => store.ReplaceAsync(Jane, Acme, [new("items.write"), new("Items.Write")], Ct));

        Assert.Equal("23514", exception.SqlState);
        Assert.Equal([new AccessGrant("items.read")], await store.ListAsync(Jane, Acme, Ct));
    }

    [Theory]
    [InlineData("insert into authz.memberships (subject, compartment) values ('x', 'Acme')", "23514")]
    [InlineData("insert into authz.memberships (subject, compartment) values ('x', 'acme-')", "23514")]
    [InlineData("insert into authz.memberships (subject, compartment) values ('x', 'ac--me')", "23514")]
    [InlineData("insert into authz.memberships (subject, compartment) values (' x', 'acme')", "23514")]
    [InlineData("insert into authz.memberships (subject, compartment) values ('', 'acme')", "23514")]
    [InlineData("insert into authz.user_grants (subject, compartment, operation, requires_second_factor) values ('x', 'acme', 'items-read', false)", "23514")]
    [InlineData("insert into authz.user_grants (subject, compartment, operation, requires_second_factor) values ('x', 'acme', 'items', false)", "23514")]
    [InlineData("insert into authz.user_grants (subject, compartment, operation, requires_second_factor) values ('x', 'acme', 'items__a.read', false)", "23514")]
    [InlineData("insert into authz.admin_grants (subject, operation, requires_second_factor) values ('x', 'a.b.c', false)", "23514")]
    [InlineData("insert into authz.user_grants (subject, compartment, operation) values ('x', 'acme', 'items.read')", "23502")]
    [InlineData("insert into authz.admin_grants (subject, operation, requires_second_factor) values ('x', 'items.read', null)", "23502")]
    [InlineData("insert into authz.client_grants (subject, space, operation) values ('inventory', 'Shared', 'users.read')", "23514")]
    [InlineData("insert into authz.client_grants (subject, space, operation) values ('inventory', '_other', 'users.read')", "23514")]
    public async Task The_schema_refuses_what_the_decision_would_never_match(string sql, string sqlState)
    {
        database.SkipWhenUnavailable();
        await using var connection = await database.OpenAsync(asApplication: true, Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(sqlState, exception.SqlState);
    }

    [Fact]
    public async Task The_schema_accepts_the_shared_segment_and_refuses_a_duplicate_key()
    {
        database.SkipWhenUnavailable();
        await using var connection = await database.OpenAsync(asApplication: true, Ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "insert into authz.client_grants (subject, space, operation) values ('inventory', '_shared', 'fiscal_years.validate')";
            await command.ExecuteNonQueryAsync(Ct);
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "insert into authz.client_grants (subject, space, operation) values ('inventory', '_shared', 'fiscal_years.validate')";
            var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Equal("23505", exception.SqlState);
        }

        Assert.True(await Services.GetRequiredService<IClientGrantStore>().HoldsAsync("inventory", Space.Shared, "fiscal_years.validate", Ct));
    }

    [Fact]
    public async Task The_application_role_has_no_right_on_the_record_of_the_installation()
    {
        database.SkipWhenUnavailable();
        await using var connection = await database.OpenAsync(asApplication: true, Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"insert into {AccessDatabase.Value.MigrationsTable} (version, script_name, checksum, applied_by) values (9, 'x', 'x', 'x')";

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal("42501", exception.SqlState);
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await database.OpenAsync(asApplication: true, Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {table}";
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task The_requirement_of_a_second_factor_is_kept_beside_each_right_and_read_back_by_the_decision()
    {
        await Projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        await Projection.ReplaceUserGrantsAsync(Jane, Acme, [AccessGrant.Of(PayrollPay), new(ItemsRead.Identity), new(ItemsRead.Identity, true)], Ct);
        await Projection.ReplaceAdminGrantsAsync(Jane, [new(UsersRead.Identity, true)], Ct);

        Assert.Equal([new AccessGrant(ItemsRead.Identity, true), new AccessGrant(PayrollPay.Identity, true)],
            await Services.GetRequiredService<IUserGrantStore>().ListAsync(Jane, Acme, Ct));
        Assert.Equal(new AccessGrant(UsersRead.Identity, true), await Services.GetRequiredService<IAdminGrantStore>().FindAsync(Jane, UsersRead.Identity, Ct));
        var decision = Services.GetRequiredService<IAccessDecision>();
        Assert.Equal(AccessErrors.SecondFactorRequiredCode, (await decision.DecideAsync(JaneActor, Acme, PayrollPay, secondFactor: false, Ct)).Error!.Code);
        Assert.True((await decision.DecideAsync(JaneActor, Acme, PayrollPay, secondFactor: true, Ct)).IsSuccess);
    }
}
