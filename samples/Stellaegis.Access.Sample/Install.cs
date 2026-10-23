using System.Data.Common;
using Npgsql;
using Stellaegis.Access.PostgreSQL;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Access.Sample;

/// <summary>
/// The installation of the example, apart from the application and under another role: the database and the
/// application's role created if missing, the <c>authz</c> schema installed by the life cycle of Primitives under
/// the administration connection, and the rights the example plays with projected — snapshots, as a registry
/// would send them. Run again, it changes nothing: the scripts are recorded, the snapshots replace themselves.
/// <code>
/// dotnet run -- install
/// </code>
/// </summary>
public static class Install
{
    public static async Task<int> RunAsync(string[] args)
    {
        _ = args;
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddUserSecrets<Inventory>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        // The application's connection string names the database and the role the application runs under; the
        // installer's connection string reaches the server under a role that may create both.
        var application = new NpgsqlConnectionStringBuilder(configuration["Stellaegis:Access:PostgreSQL:ConnectionString"]
            ?? throw new InvalidOperationException("Stellaegis:Access:PostgreSQL:ConnectionString is not configured."));
        var installer = new NpgsqlConnectionStringBuilder(configuration["Sample:Install:ConnectionString"]
            ?? throw new InvalidOperationException("Sample:Install:ConnectionString is not configured."));
        var role = application.Username ?? throw new InvalidOperationException("The application's connection string names no role.");
        var database = application.Database ?? throw new InvalidOperationException("The application's connection string names no database.");

        // 1. The database and the role, if missing.
        await using (var server = new NpgsqlConnection(installer.ConnectionString))
        {
            await server.OpenAsync();
            await using (var exists = new NpgsqlCommand("select 1 from pg_database where datname = @name", server))
            {
                exists.Parameters.AddWithValue("name", database);
                if (await exists.ExecuteScalarAsync() is null)
                {
                    await using var create = new NpgsqlCommand($"create database \"{database}\"", server);
                    await create.ExecuteNonQueryAsync();
                    Console.WriteLine($"Database {database} created.");
                }
            }
            await using (var createRole = new NpgsqlCommand(
                $"""
                do $$ begin
                  create role "{role}" login password '{application.Password?.Replace("'", "''")}';
                exception when duplicate_object or unique_violation then null;
                end $$;
                """, server))
            {
                await createRole.ExecuteNonQueryAsync();
            }
        }

        // 2. The schema, under the installer, which owns it; the application's role granted its rights.
        var adminOnDatabase = new NpgsqlConnectionStringBuilder(installer.ConnectionString) { Database = database }.ConnectionString;
        var report = await DatabaseLifecycle.InstallAsync(
            [AccessDatabase.Value], SampleFrame.Value,
            async (_, ct) => { DbConnection c = new NpgsqlConnection(adminOnDatabase); await c.OpenAsync(ct); return c; },
            role);
        foreach (var applied in report.Applied)
            Console.WriteLine($"Applied: {applied}");
        foreach (var skipped in report.Skipped)
            Console.WriteLine($"Already applied: {skipped}");

        // 3. The rights, projected as a registry would: snapshots, under the application's role. In an application
        // assembled in full, Stellaegis.Registry computes them from its roles and memberships and sends them —
        // in process, or through the projection routes; here the installation says them itself.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStellaegisFrame(SampleFrame.Value);
        services.AddStellaegisAccessPostgreSQL(configuration);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var projection = scope.ServiceProvider.GetRequiredService<IAccessProjection>();

        var person = Subject(configuration, "Sample:Person");
        var operatorOfPlatform = Subject(configuration, "Sample:Operator");
        var acme = SampleFrame.Company;

        // A person, member of the company, who reads and writes the inventory there.
        await projection.ReplaceMembershipsAsync(person, [acme]);
        await projection.ReplaceUserGrantsAsync(person, acme, [AccessGrant.Of(InventoryOperations.Read), AccessGrant.Of(InventoryOperations.Write)]);
        Console.WriteLine($"Person {person}: member of {acme}, items.read and items.write there.");

        // An operator of the platform, who purges a compartment from the shared space — with a second factor, as the
        // inventory declares it —, asks decisions and projects. Each right carries the requirement its module declares.
        await projection.ReplaceAdminGrantsAsync(operatorOfPlatform,
            [AccessGrant.Of(InventoryOperations.Purge), AccessGrant.Of(AccessOperations.DecisionsRead), AccessGrant.Of(AccessOperations.GrantsRead), AccessGrant.Of(AccessOperations.GrantsWrite)]);
        Console.WriteLine($"Operator {operatorOfPlatform}: items.purge (with a second factor), decisions.read, grants.read and grants.write in the shared space.");

        // A client of the company, named after it, which reads the inventory there and nothing else.
        await projection.ReplaceClientGrantsAsync("c_acme_robot", Space.Of(acme), [InventoryOperations.Read.Identity]);
        Console.WriteLine($"Client c_acme_robot: items.read in {acme}.");

        // The client of a registry, which projects the rights from another process.
        await projection.ReplaceClientGrantsAsync("registry", Space.Shared, [AccessOperations.GrantsRead.Identity, AccessOperations.GrantsWrite.Identity]);
        Console.WriteLine("Client registry: grants.read and grants.write in the shared space.");
        return 0;
    }

    // A person's subject is the sub of their account, which Stellaegis.Identity issues as a GUID in its N form.
    private static string Subject(IConfiguration configuration, string key)
    {
        var value = configuration[key] ?? throw new InvalidOperationException($"{key} is not configured.");
        return Guid.TryParse(value, out var id) ? id.ToString("N") : throw new InvalidOperationException($"{key} is not a GUID: {value}");
    }
}
