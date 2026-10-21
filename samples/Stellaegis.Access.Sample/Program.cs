// The example host of Stellaegis.Access: an enterprise application — one compartment, the company — whose rights
// are Access, in process on PostgreSQL. A module of its own, the inventory, has its routes decided per operation;
// the decision and projection routes of Access are mapped as a server apart would call them; and a demonstration
// token route stands in for an issuer, so that the whole plays with curl.
//
//   dotnet run -- install
//       creates the database and the application's role if missing, installs the authz schema under the
//       administration connection, and projects the rights the example plays with — what a registry does in an
//       application assembled in full. Run again, it changes nothing.
//   dotnet run
//       verifies the installation under the application's role, then serves.
//
// What a real host adds is its own: the issuer whose tokens it accepts, the registry that projects the rights.

using Stellaegis.Access.AspNetCore;
using Stellaegis.Access.PostgreSQL;
using Stellaegis.Access.Sample;
using Stellaegis.Primitives.AspNetCore;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

if (args.Length > 0 && args[0] == "install")
    return await Install.RunAsync(args[1..]);

var builder = WebApplication.CreateBuilder(args);

// The frame: one compartment, the company, and the shared space the administration of the platform acts from.
// The request context reads the actor from the token — a person by sub, a client by client_id — and the client
// acting on the actor's behalf from the act claim: the default source of Primitives reads sub alone, and a host
// that serves clients on compartmented routes declares one that reads client_id too.
builder.Services.AddStellaegisFrame(SampleFrame.Value);
builder.Services.AddStellaegisRequestContext(context => context.ActorFrom(Callers.PersonOrClient).ViaFromClaim("act"));
builder.Services.AddStellaegisModule(InventoryModule.Declaration);

// Access in process on PostgreSQL (Stellaegis:Access:PostgreSQL), as the rights system of this host: every policy
// named after an operation is answered by a decision on the actor, the space and the operation of the request.
builder.Services.AddStellaegisAccessPostgreSQL(builder.Configuration);
builder.Services.AddStellaegisAccessPolicies();

// What the host brings: the tokens it accepts — here, the ones it mints itself for the demonstration (Sample:Tokens);
// a real host validates the tokens of its issuer — and its own module.
builder.Services.AddDemoTokens(builder.Configuration);
builder.Services.AddSingleton<Inventory>();

var app = builder.Build();

// The installation ran before, under another role: prove it, under this one, before serving anything.
await app.Services.VerifyStellaegisDatabasesAsync();

app.UseAuthentication();
app.UseAuthorization();

app.MapDemoTokenEndpoint();

// Every group under one convention: a policy per operation, which Access answers.
app.MapGroup("/api").MapInventoryEndpoints()
    .RequireAuthorizationPerOperation(operation => operation.ToString());
app.MapGroup("/api/admin").MapInventoryAdminEndpoints()
    .RequireAuthorizationPerOperation(operation => operation.ToString());
app.MapGroup("/api").MapAccessDecisionEndpoints()
    .RequireAuthorizationPerOperation(operation => operation.ToString());
app.MapGroup("/api").MapAccessProjectionEndpoints()
    .RequireAuthorizationPerOperation(operation => operation.ToString());

app.Run();
return 0;
