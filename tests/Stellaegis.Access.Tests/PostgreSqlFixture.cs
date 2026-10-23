using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Stellaegis.Access.PostgreSQL;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Access.Tests;

/// <summary>The classes of the collection share one installed schema, and never run in parallel with one another.</summary>
[CollectionDefinition(PostgreSqlCollection.Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    /// <summary>The name of the collection.</summary>
    public const string Name = "PostgreSQL";
}

/// <summary>
/// A dedicated PostgreSQL database for the store tests, named by <c>STELLAEGIS_TEST_POSTGRESQL</c> — the server
/// the family's tests use, under a role that may create databases and owns what it creates — and skipped without
/// it. The database <c>stellaegis_access_tests</c> is created if missing; its <c>authz</c> schema is reinstalled
/// at every run by the installation of the module, under that connection; the tests then connect under an
/// ordinary role, <c>stellaegis_access_app</c>, created once, as an application does.
/// </summary>
public sealed class PostgreSqlFixture : IAccessDatabaseFixture
{
    private const string Variable = "STELLAEGIS_TEST_POSTGRESQL";
    private const string Database = "stellaegis_access_tests";
    private const string AppRole = "stellaegis_access_app";

    /// <summary>The connection string of the installer, which owns the schema; empty without the variable.</summary>
    public string AdminConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public string AppConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public string? Unavailable { get; private set; }

    /// <inheritdoc/>
    public void SkipWhenUnavailable() => Assert.SkipWhen(Unavailable is not null, Unavailable ?? string.Empty);

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            Unavailable = $"{Variable} is not set.";
            return;
        }

        var server = new NpgsqlConnectionStringBuilder(configured);
        AdminConnectionString = new NpgsqlConnectionStringBuilder(configured) { Database = Database }.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Username = AppRole, Password = AppRole }.ConnectionString;

        try
        {
            await using (var connection = new NpgsqlConnection(server.ConnectionString))
            {
                await connection.OpenAsync();
                await using var exists = new NpgsqlCommand("select 1 from pg_database where datname = @name", connection);
                exists.Parameters.AddWithValue("name", Database);
                if (await exists.ExecuteScalarAsync() is null)
                {
                    await using var create = new NpgsqlCommand($"create database \"{Database}\"", connection);
                    await create.ExecuteNonQueryAsync();
                }

                await using var role = new NpgsqlCommand(
                    $"""
                    do $$ begin
                      create role {AppRole} login password '{AppRole}';
                    exception when duplicate_object or unique_violation then null;
                    end $$;
                    """, connection);
                await role.ExecuteNonQueryAsync();
            }

            await using (var connection = new NpgsqlConnection(AdminConnectionString))
            {
                await connection.OpenAsync();
                await using var reset = new NpgsqlCommand($"drop schema if exists {AccessDatabase.Schema} cascade", connection);
                await reset.ExecuteNonQueryAsync();
            }

            await DatabaseLifecycle.InstallAsync([AccessDatabase.Value], Fixtures.Frame, (_, ct) => OpenAsync(asApplication: false, ct), AppRole, Actor.Job("tests"));
        }
        catch (NpgsqlException e)
        {
            Unavailable = $"No PostgreSQL for the store tests ({Variable} = '{configured}'): {e.Message}";
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public void AddStores(IServiceCollection services)
        => services.AddStellaegisAccessPostgreSQL(options => options.ConnectionString = AppConnectionString);

    /// <inheritdoc/>
    public async Task<DbConnection> OpenAsync(bool asApplication, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(asApplication ? AppConnectionString : AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(asApplication: true, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from authz.memberships; delete from authz.user_grants; delete from authz.admin_grants; delete from authz.client_grants";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
