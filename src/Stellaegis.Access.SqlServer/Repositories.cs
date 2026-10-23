using System.Data.Common;
using Dapper;

namespace Stellaegis.Access.SqlServer;

/// <summary>The rows of <c>authz.memberships</c>, read and written on the connection of a unit of work.</summary>
internal sealed class MembershipRepository(DbConnection connection, Func<DbTransaction?> transaction)
{
    public Task<bool> ExistsAsync(string subject, string compartment, CancellationToken cancellationToken)
        => connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select case when exists (select 1 from authz.memberships where subject = @subject and compartment = @compartment) then cast(1 as bit) else cast(0 as bit) end",
            new { subject, compartment }, transaction(), cancellationToken: cancellationToken));

    public async Task<IReadOnlyList<string>> ListAsync(string subject, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "select compartment from authz.memberships where subject = @subject order by compartment",
            new { subject }, transaction(), cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public Task InsertAsync(string subject, string[] compartments, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "insert into authz.memberships (subject, compartment) values (@subject, @compartment)",
            compartments.Select(compartment => new { subject, compartment }), transaction(), cancellationToken: cancellationToken));

    public Task DeleteBySubjectAsync(string subject, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "delete from authz.memberships where subject = @subject",
            new { subject }, transaction(), cancellationToken: cancellationToken));

    public Task DeleteByCompartmentAsync(string compartment, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "delete from authz.memberships where compartment = @compartment",
            new { compartment }, transaction(), cancellationToken: cancellationToken));
}

/// <summary>The rows of <c>authz.user_grants</c>: the rights of persons in a compartment.</summary>
internal sealed class UserGrantRepository(DbConnection connection, Func<DbTransaction?> transaction)
{
    public Task<GrantRow?> FindAsync(string subject, string compartment, string operation, CancellationToken cancellationToken)
        => connection.QuerySingleOrDefaultAsync<GrantRow?>(new CommandDefinition(
            "select operation as Operation, requires_second_factor as RequiresSecondFactor from authz.user_grants where subject = @subject and compartment = @compartment and operation = @operation",
            new { subject, compartment, operation }, transaction(), cancellationToken: cancellationToken));

    public async Task<IReadOnlyList<GrantRow>> ListAsync(string subject, string compartment, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<GrantRow>(new CommandDefinition(
            "select operation as Operation, requires_second_factor as RequiresSecondFactor from authz.user_grants where subject = @subject and compartment = @compartment order by operation",
            new { subject, compartment }, transaction(), cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public Task InsertAsync(string subject, string compartment, IReadOnlyList<AccessGrant> grants, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "insert into authz.user_grants (subject, compartment, operation, requires_second_factor) values (@subject, @compartment, @operation, @requires)",
            grants.Select(grant => new { subject, compartment, operation = grant.Operation, requires = grant.RequiresSecondFactor }), transaction(), cancellationToken: cancellationToken));

    public Task DeleteAsync(string subject, string compartment, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "delete from authz.user_grants where subject = @subject and compartment = @compartment",
            new { subject, compartment }, transaction(), cancellationToken: cancellationToken));

    public Task DeleteBySubjectAsync(string subject, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "delete from authz.user_grants where subject = @subject",
            new { subject }, transaction(), cancellationToken: cancellationToken));

    public Task DeleteByCompartmentAsync(string compartment, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "delete from authz.user_grants where compartment = @compartment",
            new { compartment }, transaction(), cancellationToken: cancellationToken));
}

/// <summary>The rows of <c>authz.admin_grants</c>: the rights of the operators of the platform, in the shared space.</summary>
internal sealed class AdminGrantRepository(DbConnection connection, Func<DbTransaction?> transaction)
{
    public Task<GrantRow?> FindAsync(string subject, string operation, CancellationToken cancellationToken)
        => connection.QuerySingleOrDefaultAsync<GrantRow?>(new CommandDefinition(
            "select operation as Operation, requires_second_factor as RequiresSecondFactor from authz.admin_grants where subject = @subject and operation = @operation",
            new { subject, operation }, transaction(), cancellationToken: cancellationToken));

    public Task<bool> HasAnyAsync(string subject, CancellationToken cancellationToken)
        => connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select case when exists (select 1 from authz.admin_grants where subject = @subject) then cast(1 as bit) else cast(0 as bit) end",
            new { subject }, transaction(), cancellationToken: cancellationToken));

    public async Task<IReadOnlyList<GrantRow>> ListAsync(string subject, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<GrantRow>(new CommandDefinition(
            "select operation as Operation, requires_second_factor as RequiresSecondFactor from authz.admin_grants where subject = @subject order by operation",
            new { subject }, transaction(), cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public Task InsertAsync(string subject, IReadOnlyList<AccessGrant> grants, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "insert into authz.admin_grants (subject, operation, requires_second_factor) values (@subject, @operation, @requires)",
            grants.Select(grant => new { subject, operation = grant.Operation, requires = grant.RequiresSecondFactor }), transaction(), cancellationToken: cancellationToken));

    public Task DeleteBySubjectAsync(string subject, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "delete from authz.admin_grants where subject = @subject",
            new { subject }, transaction(), cancellationToken: cancellationToken));
}

/// <summary>The rows of <c>authz.client_grants</c>: the rights of clients, in a compartment or in the shared space.</summary>
internal sealed class ClientGrantRepository(DbConnection connection, Func<DbTransaction?> transaction)
{
    public Task<bool> HoldsAsync(string subject, string space, string operation, CancellationToken cancellationToken)
        => connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select case when exists (select 1 from authz.client_grants where subject = @subject and space = @space and operation = @operation) then cast(1 as bit) else cast(0 as bit) end",
            new { subject, space, operation }, transaction(), cancellationToken: cancellationToken));

    public Task<bool> HasAnyAsync(string subject, string space, CancellationToken cancellationToken)
        => connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select case when exists (select 1 from authz.client_grants where subject = @subject and space = @space) then cast(1 as bit) else cast(0 as bit) end",
            new { subject, space }, transaction(), cancellationToken: cancellationToken));

    public async Task<IReadOnlyList<string>> ListAsync(string subject, string space, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "select operation from authz.client_grants where subject = @subject and space = @space order by operation",
            new { subject, space }, transaction(), cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public Task InsertAsync(string subject, string space, string[] operations, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "insert into authz.client_grants (subject, space, operation) values (@subject, @space, @operation)",
            operations.Select(operation => new { subject, space, operation }), transaction(), cancellationToken: cancellationToken));

    public Task DeleteAsync(string subject, string space, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "delete from authz.client_grants where subject = @subject and space = @space",
            new { subject, space }, transaction(), cancellationToken: cancellationToken));

    public Task DeleteBySubjectAsync(string subject, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "delete from authz.client_grants where subject = @subject",
            new { subject }, transaction(), cancellationToken: cancellationToken));

    public Task DeleteBySpaceAsync(string space, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            "delete from authz.client_grants where space = @space",
            new { space }, transaction(), cancellationToken: cancellationToken));
}

/// <summary>A row of <c>authz.user_grants</c> or <c>authz.admin_grants</c>, as the decision reads it.</summary>
internal sealed record GrantRow(string Operation, bool RequiresSecondFactor)
{
    public AccessGrant ToGrant() => new(Operation, RequiresSecondFactor);
}
