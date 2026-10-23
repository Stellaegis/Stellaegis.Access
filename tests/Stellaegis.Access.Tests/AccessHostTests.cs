using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Access.Tests;

/// <summary>
/// A host assembled on the stores of an engine, as an enterprise host is: the database installed by the fixture,
/// verified at startup under the application's role, and the decision running on it.
/// </summary>
public abstract class AccessHostTests(IAccessDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The declaration of the database of the engine.</summary>
    protected abstract ModuleDatabase Declaration { get; }

    /// <summary>The configuration key of the connection string of the adapter.</summary>
    protected abstract string ConnectionStringKey { get; }

    /// <summary>Registers Access on the adapter, its options bound from <paramref name="configuration"/>.</summary>
    protected abstract void AddAccess(IServiceCollection services, IConfiguration configuration);

    /// <summary>Registers Access on the adapter, its connection string given.</summary>
    protected abstract void AddAccess(IServiceCollection services, string connectionString);

    /// <summary>Makes the stores, and the verification, connect under the application's role, when the connection string does not.</summary>
    protected virtual void UnderApplicationRole(IServiceCollection services)
    {
    }

    /// <summary>The statement that renames the record of the installation away, or back.</summary>
    protected abstract string RenameRecordSql(bool back);

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ConnectionStringKey] = database.AppConnectionString,
        });
        builder.Services
            .AddStellaegisFrame(Fixtures.Frame)
            .AddStellaegisValidation()
            .AddStellaegisDatabaseVerification();
        AddAccess(builder.Services, builder.Configuration);
        UnderApplicationRole(builder.Services);
        return builder.Build();
    }

    [Fact]
    public async Task The_host_verifies_the_installed_database_at_startup_under_the_application_role()
    {
        database.SkipWhenUnavailable();
        using var host = BuildHost();

        await host.StartAsync(Ct);
        await host.StopAsync(Ct);

        Assert.Equal([Declaration], host.Services.GetStellaegisModuleDatabases());
        Assert.Contains(host.Services.GetStellaegisModules(), module => module.Module == typeof(AccessModule));
    }

    [Fact]
    public async Task A_host_whose_database_is_not_installed_refuses_to_start()
    {
        database.SkipWhenUnavailable();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddStellaegisFrame(Fixtures.Frame)
            .AddStellaegisDatabaseVerification();
        AddAccess(builder.Services, database.AppConnectionString);
        UnderApplicationRole(builder.Services);
        using var host = builder.Build();
        await using (var connection = await database.OpenAsync(asApplication: false, Ct))
        await using (var command = connection.CreateCommand())
        {
            // The record of the installation is what the verification reads first: without it, nothing was installed.
            command.CommandText = RenameRecordSql(back: false);
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
            command.CommandText = RenameRecordSql(back: true);
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public void An_empty_connection_string_refuses_the_store()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AddAccess(services, " ");
        using var provider = services.BuildServiceProvider();

        // The options are validated before the store is built: the host's validation at startup says the same.
        var exception = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => provider.GetRequiredService<IMembershipStore>());

        Assert.Contains("ConnectionString is required", exception.Message);
    }
}
