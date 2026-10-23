using Stellaegis.Primitives.Data;

namespace Stellaegis.Access.SqlServer;

/// <summary>
/// The database of the access module on SQL Server, as <c>Stellaegis.Primitives.Data</c> installs and verifies it:
/// the schema <c>authz</c> and its numbered scripts embedded in this package, the same tables as on PostgreSQL. No row-level security:
/// the module has no compartment, every row is a right the registry projected, and the application's role reads
/// them all — the space a right is granted in is a column of the key, not a policy.
/// </summary>
/// <remarks>
/// Registered by <see cref="ServiceCollectionExtensions.AddStellaegisAccessSqlServer(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.Configuration.IConfiguration, string)"/>,
/// so that a host verifies it at startup under the application's role (<c>AddStellaegisDatabaseVerification</c>).
/// An installer installs it under an administration login: <c>DatabaseLifecycle.InstallAsync([AccessDatabase.Value], frame, …)</c>.
/// Also the marker of the database for the units of work of the store.
/// </remarks>
public sealed class AccessDatabase
{
    /// <summary>The schema holding the module's tables, <c>authz</c>.</summary>
    public const string Schema = "authz";

    private AccessDatabase()
    {
    }

    /// <summary>The declaration of the database.</summary>
    public static ModuleDatabase Value { get; } = ModuleDatabase.SqlServer<AccessModule>(Schema)
        .WithScripts(SqlScripts.FromResources(typeof(AccessDatabase).Assembly, "Stellaegis.Access.SqlServer.Sql.SqlServer"));
}
