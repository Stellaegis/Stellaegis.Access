using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Stellaegis.Access.PostgreSQL;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Access.Tests;

/// <summary>The stores on PostgreSQL (<see cref="AccessStoreTests"/>), and the grammars its schema checks.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlStoreTests(PostgreSqlFixture database) : AccessStoreTests(database)
{
    private readonly PostgreSqlFixture _database = database;

    /// <inheritdoc/>
    protected override ModuleDatabase Declaration => AccessDatabase.Value;

    /// <inheritdoc/>
    protected override async Task AssertRefusedAsync(Func<Task> write, Refusal refusal)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(write);
        Assert.Equal(refusal switch
        {
            Refusal.Check => "23514",
            Refusal.Duplicate => "23505",
            _ => "42501",
        }, exception.SqlState);
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
        _database.SkipWhenUnavailable();
        await using var connection = await _database.OpenAsync(asApplication: true, Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(sqlState, exception.SqlState);
    }
}

/// <summary>A host on the PostgreSQL stores (<see cref="AccessHostTests"/>), its connection string under the application's role.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlHostTests(PostgreSqlFixture database) : AccessHostTests(database)
{
    /// <inheritdoc/>
    protected override ModuleDatabase Declaration => AccessDatabase.Value;

    /// <inheritdoc/>
    protected override string ConnectionStringKey => "Stellaegis:Access:PostgreSQL:ConnectionString";

    /// <inheritdoc/>
    protected override void AddAccess(IServiceCollection services, IConfiguration configuration) => services.AddStellaegisAccessPostgreSQL(configuration);

    /// <inheritdoc/>
    protected override void AddAccess(IServiceCollection services, string connectionString)
        => services.AddStellaegisAccessPostgreSQL(options => options.ConnectionString = connectionString);

    /// <inheritdoc/>
    protected override string RenameRecordSql(bool back) => back
        ? $"alter table {AccessDatabase.Schema}._schema_migrations_gone rename to _schema_migrations"
        : $"alter table {AccessDatabase.Value.MigrationsTable} rename to _schema_migrations_gone";
}
