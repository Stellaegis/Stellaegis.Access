using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stellaegis.Access.SqlServer;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Access.Tests;

/// <summary>
/// The stores on SQL Server (<see cref="AccessStoreTests"/>), in a case-insensitive database, their connections
/// running as the application's user; and the grammars its schema checks.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerStoreTests(SqlServerFixture database) : AccessStoreTests(database)
{
    private readonly SqlServerFixture _database = database;

    /// <inheritdoc/>
    protected override ModuleDatabase Declaration => AccessDatabase.Value;

    /// <inheritdoc/>
    protected override async Task AssertRefusedAsync(Func<Task> write, Refusal refusal)
    {
        var exception = await Assert.ThrowsAsync<SqlException>(write);
        switch (refusal)
        {
            case Refusal.Duplicate:
                Assert.Contains(exception.Number, new[] { 2627, 2601 });   // a key, a unique index
                break;
            case Refusal.Check:
                Assert.Equal(547, exception.Number);
                Assert.Contains("\"ck_", exception.Message);   // the message speaks the language of the server; the name of the constraint is ours
                break;
            default:
                Assert.Equal(229, exception.Number);   // the permission was denied
                break;
        }
    }

    // Each statement breaks one rule, the one named: its constraint, or the column a value is missing from.
    [Theory]
    [InlineData("insert into authz.memberships (subject, compartment) values (N'x', N'Acme')", "ck_memberships_compartment")]
    [InlineData("insert into authz.memberships (subject, compartment) values (N'x', N'acme-')", "ck_memberships_compartment")]
    [InlineData("insert into authz.memberships (subject, compartment) values (N'x', N'ac--me')", "ck_memberships_compartment")]
    [InlineData("insert into authz.memberships (subject, compartment) values (N' x', N'acme')", "ck_memberships_subject")]
    [InlineData("insert into authz.memberships (subject, compartment) values (N'x ', N'acme')", "ck_memberships_subject")]
    [InlineData("insert into authz.memberships (subject, compartment) values (N'', N'acme')", "ck_memberships_subject")]
    [InlineData("insert into authz.user_grants (subject, compartment, operation, requires_second_factor) values (N'x', N'acme', N'items-read', 0)", "ck_user_grants_operation")]
    [InlineData("insert into authz.user_grants (subject, compartment, operation, requires_second_factor) values (N'x', N'acme', N'items', 0)", "ck_user_grants_operation")]
    [InlineData("insert into authz.user_grants (subject, compartment, operation, requires_second_factor) values (N'x', N'acme', N'items__a.read', 0)", "ck_user_grants_operation")]
    [InlineData("insert into authz.user_grants (subject, compartment, operation, requires_second_factor) values (N'x', N'acme', N'Items.read', 0)", "ck_user_grants_operation")]
    [InlineData("insert into authz.user_grants (subject, compartment, operation, requires_second_factor) values (N'x', N'Acme', N'items.read', 0)", "ck_user_grants_compartment")]
    [InlineData("insert into authz.admin_grants (subject, operation, requires_second_factor) values (N'x', N'a.b.c', 0)", "ck_admin_grants_operation")]
    [InlineData("insert into authz.user_grants (subject, compartment, operation) values (N'x', N'acme', N'items.read')", "requires_second_factor")]
    [InlineData("insert into authz.admin_grants (subject, operation, requires_second_factor) values (N'x', N'items.read', null)", "requires_second_factor")]
    [InlineData("insert into authz.client_grants (subject, space, operation) values (N'inventory', N'Shared', N'users.read')", "ck_client_grants_space")]
    [InlineData("insert into authz.client_grants (subject, space, operation) values (N'inventory', N'_other', N'users.read')", "ck_client_grants_space")]
    [InlineData("insert into authz.client_grants (subject, space, operation) values (N'inventory', N'_SHARED', N'users.read')", "ck_client_grants_space")]
    public async Task The_schema_refuses_what_the_decision_would_never_match(string sql, string broken)
    {
        _database.SkipWhenUnavailable();
        await using var connection = await _database.OpenAsync(asApplication: true, Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(Ct));

        Assert.Equal(broken.StartsWith("ck_", StringComparison.Ordinal) ? 547 : 515, exception.Number);
        Assert.Contains(broken, exception.Message);
    }

    [Fact]
    public void An_empty_connection_string_refuses_the_store()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new SqlServerAccessStore(
            Microsoft.Extensions.Options.Options.Create(new AccessDatabaseOptions { ConnectionString = " " })));

        Assert.Contains("ConnectionString is required", exception.Message);
    }

    [Fact]
    public void The_declaration_runs_on_SqlServer_with_its_own_script()
    {
        Assert.Equal(DatabaseEngine.SqlServer, AccessDatabase.Value.Engine);
        Assert.Equal(["0001_authz.sql"], AccessDatabase.Value.Scripts.Select(script => script.FileName));
        Assert.Contains("collate Latin1_General_100_BIN2", AccessDatabase.Value.Scripts[0].Sql);
    }
}

/// <summary>
/// A host on the SQL Server stores (<see cref="AccessHostTests"/>): configured with the login of the variable, its
/// store — and so the verification at startup — connecting as the application's user.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerHostTests(SqlServerFixture database) : AccessHostTests(database)
{
    /// <inheritdoc/>
    protected override ModuleDatabase Declaration => AccessDatabase.Value;

    /// <inheritdoc/>
    protected override string ConnectionStringKey => "Stellaegis:Access:SqlServer:ConnectionString";

    /// <inheritdoc/>
    protected override void AddAccess(IServiceCollection services, IConfiguration configuration) => services.AddStellaegisAccessSqlServer(configuration);

    /// <inheritdoc/>
    protected override void AddAccess(IServiceCollection services, string connectionString)
        => services.AddStellaegisAccessSqlServer(options => options.ConnectionString = connectionString);

    /// <inheritdoc/>
    protected override void UnderApplicationRole(IServiceCollection services) => database.UnderApplicationRole(services);

    /// <inheritdoc/>
    protected override string RenameRecordSql(bool back) => back
        ? $"exec sp_rename N'{AccessDatabase.Schema}._schema_migrations_gone', N'_schema_migrations'"
        : $"exec sp_rename N'{AccessDatabase.Value.MigrationsTable}', N'_schema_migrations_gone'";
}
