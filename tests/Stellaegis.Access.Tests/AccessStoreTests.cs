using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using static Stellaegis.Access.Tests.Fixtures;

namespace Stellaegis.Access.Tests;

/// <summary>A database of an engine for the store tests, installed by the installation of the module.</summary>
public interface IAccessDatabaseFixture : IAsyncLifetime
{
    /// <summary>The connection string a host is configured with.</summary>
    string AppConnectionString { get; }

    /// <summary>Why the database cannot be used, or <see langword="null"/> when it can.</summary>
    string? Unavailable { get; }

    /// <summary>Skips the calling test when the database cannot be used.</summary>
    void SkipWhenUnavailable();

    /// <summary>Registers the stores of the engine, connecting under the application's role.</summary>
    void AddStores(IServiceCollection services);

    /// <summary>Opens a connection to the test database, under the application's role or the installer's.</summary>
    Task<DbConnection> OpenAsync(bool asApplication, CancellationToken cancellationToken);

    /// <summary>Empties the four tables, under the application's role, so that each test starts from nothing.</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>What the schema refuses, whatever the engine says it with.</summary>
public enum Refusal
{
    /// <summary>A value its check refuses.</summary>
    Check,

    /// <summary>A key already taken.</summary>
    Duplicate,

    /// <summary>A right the role does not hold.</summary>
    PermissionDenied,
}

/// <summary>
/// The decision and the projection against a real engine, under the application's role: the steps replayed on the
/// four tables, the snapshots, the removals, and what the schema itself guarantees. Every engine runs the same
/// scenarios; a derived class says how its engine refuses, and checks its own grammars.
/// </summary>
public abstract class AccessStoreTests(IAccessDatabaseFixture database) : IAsyncLifetime
{
    /// <summary>Asserts that <paramref name="write"/> fails as the engine says <paramref name="refusal"/>.</summary>
    protected abstract Task AssertRefusedAsync(Func<Task> write, Refusal refusal);

    /// <summary>The declaration of the database of the engine.</summary>
    protected abstract ModuleDatabase Declaration { get; }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

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
                database.AddStores(services);
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
        GC.SuppressFinalize(this);
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
        await AssertRefusedAsync(() => store.ReplaceAsync(Jane, Acme, [new("items.write"), new("Items.Write")], Ct), Refusal.Check);

        Assert.Equal([new AccessGrant("items.read")], await store.ListAsync(Jane, Acme, Ct));
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
            await AssertRefusedAsync(() => command.ExecuteNonQueryAsync(Ct), Refusal.Duplicate);
        }

        Assert.True(await Services.GetRequiredService<IClientGrantStore>().HoldsAsync("inventory", Space.Shared, "fiscal_years.validate", Ct));
    }

    [Fact]
    public async Task The_application_role_has_no_right_on_the_record_of_the_installation()
    {
        database.SkipWhenUnavailable();
        await using var connection = await database.OpenAsync(asApplication: true, Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"insert into {Declaration.MigrationsTable} (version, script_name, checksum, applied_by) values (9, 'x', 'x', 'x')";

        await AssertRefusedAsync(() => command.ExecuteNonQueryAsync(Ct), Refusal.PermissionDenied);
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await database.OpenAsync(asApplication: true, Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
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
