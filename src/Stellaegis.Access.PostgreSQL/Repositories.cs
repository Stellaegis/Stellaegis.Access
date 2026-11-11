using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace Stellaegis.Access.PostgreSQL;

/// <summary>The rows of <c>authz.memberships</c>, read and written on the connection of a unit of work.</summary>
internal sealed class MembershipRepository(NpgsqlConnection connection, Func<NpgsqlTransaction?> transaction)
{
    public async Task<bool> ExistsAsync(string subject, string compartment, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select exists (select 1 from authz.memberships where subject = @subject and compartment = @compartment)",
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("compartment", NpgsqlDbType.Text).Value = compartment;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<string>> ListAsync(string subject, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select compartment from authz.memberships where subject = @subject order by compartment",
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var compartments = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            compartments.Add(reader.GetString("compartment"));
        return compartments;
    }

    public async Task InsertAsync(string subject, string[] compartments, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into authz.memberships (subject, compartment)
            select @subject, unnest(@compartments)
            """,
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("compartments", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = compartments;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("delete from authz.memberships where subject = @subject", connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteByCompartmentAsync(string compartment, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("delete from authz.memberships where compartment = @compartment", connection, transaction());
        command.Parameters.Add("compartment", NpgsqlDbType.Text).Value = compartment;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The rows of <c>authz.user_grants</c>: the rights of persons in a compartment.</summary>
internal sealed class UserGrantRepository(NpgsqlConnection connection, Func<NpgsqlTransaction?> transaction)
{
    public async Task<GrantRow?> FindAsync(string subject, string compartment, string operation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"select {GrantRow.Columns} from authz.user_grants where subject = @subject and compartment = @compartment and operation = @operation",
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("compartment", NpgsqlDbType.Text).Value = compartment;
        command.Parameters.Add("operation", NpgsqlDbType.Text).Value = operation;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? GrantRow.Read(reader) : null;
    }

    public async Task<IReadOnlyList<GrantRow>> ListAsync(string subject, string compartment, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"select {GrantRow.Columns} from authz.user_grants where subject = @subject and compartment = @compartment order by operation",
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("compartment", NpgsqlDbType.Text).Value = compartment;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var grants = new List<GrantRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            grants.Add(GrantRow.Read(reader));
        return grants;
    }

    public async Task InsertAsync(string subject, string compartment, string[] operations, bool[] requires, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into authz.user_grants (subject, compartment, operation, requires_second_factor)
            select @subject, @compartment, g.operation, g.requires from unnest(@operations, @requires) as g (operation, requires)
            """,
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("compartment", NpgsqlDbType.Text).Value = compartment;
        command.Parameters.Add("operations", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = operations;
        command.Parameters.Add("requires", NpgsqlDbType.Array | NpgsqlDbType.Boolean).Value = requires;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string subject, string compartment, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("delete from authz.user_grants where subject = @subject and compartment = @compartment", connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("compartment", NpgsqlDbType.Text).Value = compartment;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("delete from authz.user_grants where subject = @subject", connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteByCompartmentAsync(string compartment, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("delete from authz.user_grants where compartment = @compartment", connection, transaction());
        command.Parameters.Add("compartment", NpgsqlDbType.Text).Value = compartment;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The rows of <c>authz.admin_grants</c>: the rights of the operators of the platform, in the shared space.</summary>
internal sealed class AdminGrantRepository(NpgsqlConnection connection, Func<NpgsqlTransaction?> transaction)
{
    public async Task<GrantRow?> FindAsync(string subject, string operation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"select {GrantRow.Columns} from authz.admin_grants where subject = @subject and operation = @operation",
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("operation", NpgsqlDbType.Text).Value = operation;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? GrantRow.Read(reader) : null;
    }

    public async Task<bool> HasAnyAsync(string subject, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select exists (select 1 from authz.admin_grants where subject = @subject)",
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<GrantRow>> ListAsync(string subject, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"select {GrantRow.Columns} from authz.admin_grants where subject = @subject order by operation",
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var grants = new List<GrantRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            grants.Add(GrantRow.Read(reader));
        return grants;
    }

    public async Task InsertAsync(string subject, string[] operations, bool[] requires, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into authz.admin_grants (subject, operation, requires_second_factor)
            select @subject, g.operation, g.requires from unnest(@operations, @requires) as g (operation, requires)
            """,
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("operations", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = operations;
        command.Parameters.Add("requires", NpgsqlDbType.Array | NpgsqlDbType.Boolean).Value = requires;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("delete from authz.admin_grants where subject = @subject", connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The rows of <c>authz.client_grants</c>: the rights of clients, in a compartment or in the shared space.</summary>
internal sealed class ClientGrantRepository(NpgsqlConnection connection, Func<NpgsqlTransaction?> transaction)
{
    public async Task<bool> HoldsAsync(string subject, string space, string operation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select exists (select 1 from authz.client_grants where subject = @subject and space = @space and operation = @operation)",
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("space", NpgsqlDbType.Text).Value = space;
        command.Parameters.Add("operation", NpgsqlDbType.Text).Value = operation;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<bool> HasAnyAsync(string subject, string space, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select exists (select 1 from authz.client_grants where subject = @subject and space = @space)",
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("space", NpgsqlDbType.Text).Value = space;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<string>> ListAsync(string subject, string space, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select operation from authz.client_grants where subject = @subject and space = @space order by operation",
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("space", NpgsqlDbType.Text).Value = space;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var operations = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            operations.Add(reader.GetString("operation"));
        return operations;
    }

    public async Task InsertAsync(string subject, string space, string[] operations, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into authz.client_grants (subject, space, operation)
            select @subject, @space, unnest(@operations)
            """,
            connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("space", NpgsqlDbType.Text).Value = space;
        command.Parameters.Add("operations", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = operations;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string subject, string space, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("delete from authz.client_grants where subject = @subject and space = @space", connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        command.Parameters.Add("space", NpgsqlDbType.Text).Value = space;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("delete from authz.client_grants where subject = @subject", connection, transaction());
        command.Parameters.Add("subject", NpgsqlDbType.Text).Value = subject;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBySpaceAsync(string space, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("delete from authz.client_grants where space = @space", connection, transaction());
        command.Parameters.Add("space", NpgsqlDbType.Text).Value = space;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A row of <c>authz.user_grants</c> or <c>authz.admin_grants</c>, as the decision reads it.</summary>
internal sealed record GrantRow(string Operation, bool RequiresSecondFactor)
{
    /// <summary>The columns <see cref="Read"/> reads.</summary>
    public const string Columns = "operation, requires_second_factor";

    public static GrantRow Read(NpgsqlDataReader row) => new(row.GetString("operation"), row.GetBoolean("requires_second_factor"));

    public AccessGrant ToGrant() => new(Operation, RequiresSecondFactor);
}
