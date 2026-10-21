using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stellaegis.Access.PostgreSQL;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Access.Tests;

/// <summary>
/// A host assembled on the PostgreSQL stores, as an enterprise host is: the database installed by the fixture,
/// verified at startup under the application's role, and the decision running on it.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlHostTests(PostgreSqlFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stellaegis:Access:PostgreSQL:ConnectionString"] = database.AppConnectionString,
        });
        builder.Services
            .AddStellaegisFrame(PostgreSqlFixture.Frame)
            .AddStellaegisValidation()
            .AddStellaegisDatabaseVerification()
            .AddStellaegisAccessPostgreSQL(builder.Configuration);
        return builder.Build();
    }

    [Fact]
    public async Task The_host_verifies_the_installed_database_at_startup_under_the_application_role()
    {
        database.SkipWhenUnavailable();
        using var host = BuildHost();

        await host.StartAsync(Ct);
        await host.StopAsync(Ct);

        Assert.Equal([AccessDatabase.Value], host.Services.GetStellaegisModuleDatabases());
        Assert.Contains(host.Services.GetStellaegisModules(), module => module.Module == typeof(AccessModule));
    }

    [Fact]
    public async Task A_host_whose_database_is_not_installed_refuses_to_start()
    {
        database.SkipWhenUnavailable();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddStellaegisFrame(PostgreSqlFixture.Frame)
            .AddStellaegisDatabaseVerification()
            .AddStellaegisAccessPostgreSQL(options => options.ConnectionString = database.AppConnectionString);
        using var host = builder.Build();
        await using (var connection = await database.OpenAsync(asApplication: false, Ct))
        await using (var command = connection.CreateCommand())
        {
            // The record of the installation is what the verification reads first: without it, nothing was installed.
            command.CommandText = $"alter table {AccessDatabase.Value.MigrationsTable} rename to _schema_migrations_gone";
            await command.ExecuteNonQueryAsync(Ct);
        }

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(Ct));
            Assert.Contains("access is not installed", exception.Message);
        }
        finally
        {
            await using var connection = await database.OpenAsync(asApplication: false, Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "alter table authz._schema_migrations_gone rename to _schema_migrations";
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public void An_empty_connection_string_refuses_the_store()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStellaegisAccessPostgreSQL(options => options.ConnectionString = " ");
        using var provider = services.BuildServiceProvider();

        // The options are validated before the store is built: the host's validation at startup says the same.
        var exception = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => provider.GetRequiredService<IMembershipStore>());

        Assert.Contains("ConnectionString is required", exception.Message);
    }
}
