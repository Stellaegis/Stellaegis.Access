using System.Data.Common;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Access.SqlServer;

/// <summary>
/// The four stores of the rights on SQL Server, in the schema <c>authz</c>: one object, one connection string, the
/// repositories of <see cref="AccessDatabase"/> handed out by a unit of work of <c>Stellaegis.Primitives.Data</c>.
/// Each read of a decision is one query on its own unit of work; each replacement of a snapshot is one transaction
/// — delete, then insert — so that a decision never reads a subject half projected. No connection outlives a call,
/// and none is shared between two.
/// </summary>
internal sealed class SqlServerAccessStore
    : IMembershipStore, IUserGrantStore, IAdminGrantStore, IClientGrantStore
{
    private readonly Func<CancellationToken, Task<DbConnection>> _open;
    private readonly AdoUnitOfWorkFactory<AccessDatabase> _units;

    /// <summary>Creates the store from options the host validated at startup, or that a caller built by hand.</summary>
    /// <exception cref="InvalidOperationException">The options do not hold: <see cref="AccessDatabaseOptions.FindError"/> says why.</exception>
    public SqlServerAccessStore(IOptions<AccessDatabaseOptions> options)
        : this(OpenFrom(Guard.NotNull(options).Value))
    {
    }

    /// <summary>Creates the store on connections <paramref name="open"/> opens — the tests take the application's identity on them.</summary>
    internal SqlServerAccessStore(Func<CancellationToken, Task<DbConnection>> open)
    {
        _open = Guard.NotNull(open);
        var registry = new RepositoryRegistry<AccessDatabase>()
            .Add<MembershipRepository>((connection, transaction) => new MembershipRepository(connection, transaction))
            .Add<UserGrantRepository>((connection, transaction) => new UserGrantRepository(connection, transaction))
            .Add<AdminGrantRepository>((connection, transaction) => new AdminGrantRepository(connection, transaction))
            .Add<ClientGrantRepository>((connection, transaction) => new ClientGrantRepository(connection, transaction));

        _units = new AdoUnitOfWorkFactory<AccessDatabase>(_open, registry);
    }

    /// <summary>Opens a connection under the application's role, as the verification of the database needs one.</summary>
    public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) => _open(cancellationToken);

    private static Func<CancellationToken, Task<DbConnection>> OpenFrom(AccessDatabaseOptions options)
    {
        if (options.FindError() is { } error)
            throw new InvalidOperationException($"{nameof(AccessDatabaseOptions)}: {error}");
        var connectionString = options.ConnectionString;
        return async cancellationToken =>
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        };
    }

    // ── Memberships ──────────────────────────────────────────────────────────

    async Task<bool> IMembershipStore.IsMemberAsync(string subject, Compartment compartment, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        Guard.NotNull(compartment);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await uow.Repo<MembershipRepository>().ExistsAsync(subject, compartment.Value, cancellationToken).ConfigureAwait(false);
    }

    async Task<IReadOnlyList<Compartment>> IMembershipStore.ListAsync(string subject, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        var values = await uow.Repo<MembershipRepository>().ListAsync(subject, cancellationToken).ConfigureAwait(false);
        return values.Select(Compartment.Parse).ToList();
    }

    async Task IMembershipStore.ReplaceAsync(string subject, IReadOnlyCollection<Compartment> compartments, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        Guard.NotNull(compartments);
        await using var uow = await _units.OpenAndBeginAsync(cancellationToken).ConfigureAwait(false);
        var memberships = uow.Repo<MembershipRepository>();
        await memberships.DeleteBySubjectAsync(subject, cancellationToken).ConfigureAwait(false);
        if (compartments.Count > 0)
            await memberships.InsertAsync(subject, compartments.Select(c => c.Value).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task IMembershipStore.RemoveCompartmentAsync(Compartment compartment, CancellationToken cancellationToken)
    {
        Guard.NotNull(compartment);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        await uow.Repo<MembershipRepository>().DeleteByCompartmentAsync(compartment.Value, cancellationToken).ConfigureAwait(false);
    }

    async Task IMembershipStore.RemoveSubjectAsync(string subject, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        await uow.Repo<MembershipRepository>().DeleteBySubjectAsync(subject, cancellationToken).ConfigureAwait(false);
    }

    // ── Rights of persons in a compartment ───────────────────────────────────

    async Task<AccessGrant?> IUserGrantStore.FindAsync(string subject, Compartment compartment, string operation, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        Guard.NotNull(compartment);
        Guard.NotWhiteSpace(operation);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        return (await uow.Repo<UserGrantRepository>().FindAsync(subject, compartment.Value, operation, cancellationToken).ConfigureAwait(false))?.ToGrant();
    }

    async Task<IReadOnlyList<AccessGrant>> IUserGrantStore.ListAsync(string subject, Compartment compartment, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        Guard.NotNull(compartment);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        return (await uow.Repo<UserGrantRepository>().ListAsync(subject, compartment.Value, cancellationToken).ConfigureAwait(false)).Select(row => row.ToGrant()).ToList();
    }

    async Task IUserGrantStore.ReplaceAsync(string subject, Compartment compartment, IReadOnlyCollection<AccessGrant> grants, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        Guard.NotNull(compartment);
        Guard.NotNull(grants);
        await using var uow = await _units.OpenAndBeginAsync(cancellationToken).ConfigureAwait(false);
        var rows = uow.Repo<UserGrantRepository>();
        await rows.DeleteAsync(subject, compartment.Value, cancellationToken).ConfigureAwait(false);
        if (grants.Count > 0)
        {
            await rows.InsertAsync(subject, compartment.Value, AccessGrant.Merge(grants), cancellationToken).ConfigureAwait(false);
        }
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task IUserGrantStore.RemoveCompartmentAsync(Compartment compartment, CancellationToken cancellationToken)
    {
        Guard.NotNull(compartment);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        await uow.Repo<UserGrantRepository>().DeleteByCompartmentAsync(compartment.Value, cancellationToken).ConfigureAwait(false);
    }

    async Task IUserGrantStore.RemoveSubjectAsync(string subject, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        await uow.Repo<UserGrantRepository>().DeleteBySubjectAsync(subject, cancellationToken).ConfigureAwait(false);
    }

    // ── Rights of the operators of the platform ──────────────────────────────

    async Task<AccessGrant?> IAdminGrantStore.FindAsync(string subject, string operation, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        Guard.NotWhiteSpace(operation);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        return (await uow.Repo<AdminGrantRepository>().FindAsync(subject, operation, cancellationToken).ConfigureAwait(false))?.ToGrant();
    }

    async Task<bool> IAdminGrantStore.HasAnyAsync(string subject, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await uow.Repo<AdminGrantRepository>().HasAnyAsync(subject, cancellationToken).ConfigureAwait(false);
    }

    async Task<IReadOnlyList<AccessGrant>> IAdminGrantStore.ListAsync(string subject, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        return (await uow.Repo<AdminGrantRepository>().ListAsync(subject, cancellationToken).ConfigureAwait(false)).Select(row => row.ToGrant()).ToList();
    }

    async Task IAdminGrantStore.ReplaceAsync(string subject, IReadOnlyCollection<AccessGrant> grants, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        Guard.NotNull(grants);
        await using var uow = await _units.OpenAndBeginAsync(cancellationToken).ConfigureAwait(false);
        var rows = uow.Repo<AdminGrantRepository>();
        await rows.DeleteBySubjectAsync(subject, cancellationToken).ConfigureAwait(false);
        if (grants.Count > 0)
        {
            await rows.InsertAsync(subject, AccessGrant.Merge(grants), cancellationToken).ConfigureAwait(false);
        }
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task IAdminGrantStore.RemoveSubjectAsync(string subject, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(subject);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        await uow.Repo<AdminGrantRepository>().DeleteBySubjectAsync(subject, cancellationToken).ConfigureAwait(false);
    }

    // ── Rights of clients ────────────────────────────────────────────────────

    async Task<bool> IClientGrantStore.HoldsAsync(string clientId, Space space, string operation, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(clientId);
        Guard.NotNull(space);
        Guard.NotWhiteSpace(operation);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await uow.Repo<ClientGrantRepository>().HoldsAsync(clientId, space.ToSegment(), operation, cancellationToken).ConfigureAwait(false);
    }

    async Task<bool> IClientGrantStore.HasAnyAsync(string clientId, Space space, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(clientId);
        Guard.NotNull(space);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await uow.Repo<ClientGrantRepository>().HasAnyAsync(clientId, space.ToSegment(), cancellationToken).ConfigureAwait(false);
    }

    async Task<IReadOnlyList<string>> IClientGrantStore.ListAsync(string clientId, Space space, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(clientId);
        Guard.NotNull(space);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await uow.Repo<ClientGrantRepository>().ListAsync(clientId, space.ToSegment(), cancellationToken).ConfigureAwait(false);
    }

    async Task IClientGrantStore.ReplaceAsync(string clientId, Space space, IReadOnlyCollection<string> operations, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(clientId);
        Guard.NotNull(space);
        Guard.NotNull(operations);
        await using var uow = await _units.OpenAndBeginAsync(cancellationToken).ConfigureAwait(false);
        var grants = uow.Repo<ClientGrantRepository>();
        await grants.DeleteAsync(clientId, space.ToSegment(), cancellationToken).ConfigureAwait(false);
        if (operations.Count > 0)
            await grants.InsertAsync(clientId, space.ToSegment(), Distinct(operations), cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task IClientGrantStore.RemoveCompartmentAsync(Compartment compartment, CancellationToken cancellationToken)
    {
        Guard.NotNull(compartment);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        await uow.Repo<ClientGrantRepository>().DeleteBySpaceAsync(compartment.Value, cancellationToken).ConfigureAwait(false);
    }

    async Task IClientGrantStore.RemoveSubjectAsync(string clientId, CancellationToken cancellationToken)
    {
        Guard.NotWhiteSpace(clientId);
        await using var uow = await _units.OpenAsync(cancellationToken).ConfigureAwait(false);
        await uow.Repo<ClientGrantRepository>().DeleteBySubjectAsync(clientId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The operations once each: a snapshot that names one twice would violate the key, not the meaning.</summary>
    private static string[] Distinct(IReadOnlyCollection<string> operations)
        => operations.Distinct(StringComparer.Ordinal).ToArray();

}
