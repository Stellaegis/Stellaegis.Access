using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;

namespace Stellaegis.Access.Sample;

/// <summary>The frame of the example: one compartment, the company, and the shared space of the platform.</summary>
public static class SampleFrame
{
    public static readonly Compartment Company = Compartment.Parse("acme");

    public static readonly ApplicationFrame Value = ApplicationFrame.SingleCompartment(Company).WithSharedSpace();
}

/// <summary>
/// The module of the example, compartmented: an inventory of items, held per compartment. It declares its
/// operations, labels its routes with them, and decides nothing — Access does.
/// </summary>
public sealed class InventoryModule : IStellaegisModule
{
    public static string Name => "inventory";

    public static ModuleDeclaration Declaration { get; } = ModuleDeclaration.Compartmented<InventoryModule>()
        .WithOperations(InventoryOperations.All);
}

/// <summary>
/// The operations of the inventory: what a registry grants, by identity. Purging a compartment is the one sensitive
/// enough to require a session opened with a second factor, whoever holds it.
/// </summary>
public static class InventoryOperations
{
    public static readonly Operation Read = new("items", "read", OperationKind.Read);
    public static readonly Operation Write = new("items", "write", OperationKind.Write);
    public static readonly Operation Purge = new("items", "purge", OperationKind.Delete, requiresSecondFactor: true);
    public static IReadOnlyList<Operation> All { get; } = [Read, Write, Purge];
}

/// <summary>The items, in memory, per compartment: enough for the decisions to be seen.</summary>
public sealed class Inventory
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Item>> _items = new();

    public IReadOnlyList<Item> List(Compartment compartment)
        => _items.TryGetValue(compartment.Value, out var items) ? [.. items] : [];

    public Item Add(Compartment compartment, string name, Actor by)
    {
        var item = new Item(Guid.NewGuid().ToString("N"), name, by.Value, by.Via?.Value);
        _items.GetOrAdd(compartment.Value, _ => new ConcurrentQueue<Item>()).Enqueue(item);
        return item;
    }

    public int Purge(Compartment compartment)
        => _items.TryRemove(compartment.Value, out var items) ? items.Count : 0;

    public sealed record Item(string Id, string Name, string AddedBy, string? Via);
}

/// <summary>What a request adds to the inventory.</summary>
public sealed record NewItem(string Name);

/// <summary>The routes of the inventory, each under its operation.</summary>
public static class InventoryEndpoints
{
    /// <summary>The routes in the compartment: read and write, decided for the actor of the request in its space.</summary>
    public static RouteGroupBuilder MapInventoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/items")
            .BelongsTo<InventoryModule>()
            .RequireRequestContext()
            .LoadsNoResource();

        group.MapGet("/", (IRequestContext context, Inventory inventory) =>
                context.Space?.Compartment is { } compartment
                    ? Results.Ok(new
                    {
                        actor = context.Actor.Value,
                        via = context.Actor.Via?.Value,
                        space = context.Space.ToSegment(),
                        items = inventory.List(compartment),
                    })
                    : Results.NotFound())
            .WithMetadata(InventoryOperations.Read);

        group.MapPost("/", (NewItem item, IRequestContext context, Inventory inventory) =>
                context.Space?.Compartment is { } compartment && !string.IsNullOrWhiteSpace(item?.Name)
                    ? Results.Created($"/api/items/{compartment.Value}", inventory.Add(compartment, item.Name, context.Actor))
                    : Results.BadRequest())
            .WithMetadata(InventoryOperations.Write);

        return group;
    }

    /// <summary>
    /// The administration route, which acts on a compartment named in its path: purging it. The compartment is the
    /// object of the route, not the space of the decision — Access decides it in the shared space, for the operators
    /// of the platform; a member of the compartment holding <c>items.purge</c> there does not purge it.
    /// </summary>
    public static RouteGroupBuilder MapInventoryAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/inventory")
            .BelongsTo<InventoryModule>()
            .RequireRequestContext()
            .ActsOnSpaces()
            .LoadsNoResource();

        group.MapDelete("/compartments/{compartment}", (Compartment compartment, IRequestContext context, Inventory inventory) =>
                Results.Ok(new { purged = inventory.Purge(compartment), compartment = compartment.Value, actor = context.Actor.Value }))
            .WithMetadata(InventoryOperations.Purge);

        return group;
    }
}

/// <summary>Reads the actor of a request from the claims of the token this host accepts.</summary>
public static class Callers
{
    /// <summary>
    /// A person by <c>sub</c>, or a client by <c>client_id</c>: the default source of Primitives reads <c>sub</c> and
    /// <c>NameIdentifier</c> only, and would fail the request of a client on a compartmented route.
    /// </summary>
    public static Actor? PersonOrClient(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true)
            return null;
        var value = http.User.FindFirst("sub")?.Value ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? http.User.FindFirst("client_id")?.Value;
        return value is null ? null : Actor.Parse(value);
    }
}

/// <summary>
/// The tokens of the demonstration: this host mints a bearer for whoever asks, and validates it, so that the
/// decisions play with curl. A real host accepts the tokens of its issuer — <c>Stellaegis.Issuer</c> — and mints
/// none: this route is the one thing of the example that must never reach a deployment.
/// </summary>
public static class DemoTokens
{
    private const string Issuer = "stellaegis-access-sample";
    private const string Audience = "stellaegis-access-sample";

    /// <summary>Registers the bearer authentication on the key of <c>Sample:Tokens:Key</c>.</summary>
    public static IServiceCollection AddDemoTokens(this IServiceCollection services, IConfiguration configuration)
    {
        var key = SigningKey(configuration);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;   // sub stays sub, client_id stays client_id, act stays act
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = key,
                NameClaimType = "sub",
            };
        });
        return services;
    }

    /// <summary>
    /// <c>POST /demo/token?subject=…</c> for a person, <c>?clientId=…</c> for a client, <c>&amp;act=…</c> for the
    /// client acting on the person's behalf, <c>&amp;amr=pwd,otp,mfa</c> for the methods the person's session proved —
    /// what an issuer writes from the sign-in. Anonymous, by construction: it is the demonstration.
    /// </summary>
    public static IEndpointConventionBuilder MapDemoTokenEndpoint(this IEndpointRouteBuilder routes)
        => routes.MapPost("/demo/token", (string? subject, string? clientId, string? act, string? amr, IConfiguration configuration) =>
            {
                if (string.IsNullOrWhiteSpace(subject) == string.IsNullOrWhiteSpace(clientId))
                    return Results.BadRequest(new { error = "Name a subject (a person) or a clientId (a client), one of the two." });

                var claims = new List<Claim> { subject is not null ? new Claim("sub", subject) : new Claim("client_id", clientId!) };
                if (!string.IsNullOrWhiteSpace(act))
                    claims.Add(new Claim("act", act));
                if (subject is not null && !string.IsNullOrWhiteSpace(amr))
                    claims.AddRange(amr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(method => new Claim("amr", method)));

                var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = Issuer,
                    Audience = Audience,
                    Subject = new ClaimsIdentity(claims),
                    Expires = DateTime.UtcNow.AddHours(1),
                    SigningCredentials = new SigningCredentials(SigningKey(configuration), SecurityAlgorithms.HmacSha256),
                });
                return Results.Ok(new { access_token = token, token_type = "Bearer", expires_in = 3600 });
            })
            .AllowAnonymous();

    private static SymmetricSecurityKey SigningKey(IConfiguration configuration)
    {
        var key = configuration["Sample:Tokens:Key"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("Sample:Tokens:Key is required, at least 32 bytes long.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }
}
