# Stellaegis.Access

Stellaegis.Access is the decision point of a .NET application assembled on
[Stellaegis.Primitives](https://github.com/Stellaegis/Stellaegis.Primitives): one question — an actor, a space, an
operation — and one answer, yes or no, with a code. It holds the effective rights a registry projects into it, and
decides on them; it is the rights system that answers the policy per operation
`Stellaegis.Primitives.AspNetCore` attaches to a module's routes.

It knows neither accounts (`Stellaegis.Identity`), nor tokens (`Stellaegis.Issuer`), nor roles nor catalogue
(`Stellaegis.Registry`). And it stops at the rights of calling: whether an actor may perform an operation in a
space. What they may do with the data a route loads is the application's, through the resource hook of Primitives.

The library targets .NET 10 and is released under the MIT license.

## Packages

| Package | Contents | Depends on |
| --- | --- | --- |
| `Stellaegis.Access` | The decision, the four stores as ports and in memory, the projection by snapshots and its events, the module and its operations | `Stellaegis.Primitives`, `Stellaegis.Primitives.Hosting` |
| `Stellaegis.Access.PostgreSQL` | The stores on PostgreSQL, and the `authz` schema declared as a module database for the installation and the verification | `Stellaegis.Primitives.Data`, Npgsql, Dapper |
| `Stellaegis.Access.AspNetCore` | Access as the rights system of a host — the policy per operation, answered from the request —, the decision routes a resource server apart calls, the projection routes a registry in another process writes through | `Stellaegis.Primitives.AspNetCore`, ASP.NET Core |
| `Stellaegis.Access.Client` | The decision over HTTP, under the same port as in process, behind a cache of the decisions that the events of Access invalidate | `Stellaegis.Access`, `Stellaegis.Primitives.Hosting`, `Stellaegis.Caching` |

The four packages share one version, and follow the major version of Stellaegis.Primitives: 5.0.0 runs on
Primitives 5.1.0.

## A host, in full

An enterprise application — one compartment, the company, and the shared space of the platform — whose rights are
Access, in process. This is `samples/Stellaegis.Access.Sample`, which runs as is on a PostgreSQL server and plays
with `curl` ([samples/README.md](samples/README.md)).

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStellaegisFrame(ApplicationFrame.SingleCompartment("acme").WithSharedSpace());
builder.Services.AddStellaegisRequestContext(c => c.ActorFrom(PersonOrClient).ViaFromClaim("act"));
builder.Services.AddStellaegisModule(InventoryModule.Declaration);        // the host's module, its operations declared

builder.Services.AddStellaegisAccessPostgreSQL(builder.Configuration);    // Stellaegis:Access:PostgreSQL
builder.Services.AddStellaegisAccessPolicies();                            // every policy named after an operation is Access's to answer

builder.Services.AddAuthentication(...).AddJwtBearer(...);                // the tokens of the host's issuer

var app = builder.Build();
await app.Services.VerifyStellaegisDatabasesAsync();   // the installation ran before, under another role: prove it

app.UseAuthentication();
app.UseAuthorization();

app.MapGroup("/api").MapInventoryEndpoints()
   .RequireAuthorizationPerOperation(operation => operation.ToString());   // "items.read", "items.write", …
app.MapGroup("/api/admin").MapInventoryAdminEndpoints()
   .RequireAuthorizationPerOperation(operation => operation.ToString());   // "items.purge", acting on a compartment
app.MapGroup("/api").MapAccessDecisionEndpoints()                          // what a resource server apart calls
   .RequireAuthorizationPerOperation(operation => operation.ToString());
app.MapGroup("/api").MapAccessProjectionEndpoints()                        // what a registry apart writes through
   .RequireAuthorizationPerOperation(operation => operation.ToString());

app.Run();
```

What the host brings is its own: the tokens it accepts, its modules, and the registry that projects the rights.

## The decision

Three steps, each monotone — a step never widens what the previous one allowed:

1. **The actor is admitted in the space.** In a compartment, a person is admitted by a membership, a client by
   holding any right there. In the shared space, a person is admitted by holding any right of an operator of the
   platform, a client by holding any right there. Not admitted: `Access.NotMember`, whatever the operation.
2. **The actor holds the operation in the space.** Read from the domain of the actor: a person in a compartment
   from the user grants, a person in the shared space from the admin grants, a client from the client grants.
   Not held: `Access.Denied`.
3. **The client acting on the actor's behalf holds it too.** When the actor comes through a client — the
   `Actor.Via` of Primitives, the `act` claim of a token exchange — the client must hold the operation in the space
   as well: the rights of a person are restricted by those of the client acting for them, never widened. Not held:
   `Access.ViaDenied`.

```csharp
Result decision = await access.DecideAsync(actor, space, InventoryOperations.Write, ct);
```

`IAccessDecision` is the port, in process and over HTTP alike. A refusal never says which right would have been
needed.

Whether a client may act on a person's behalf at all is not Access's to say: it is an attribute of the client at
the issuer, checked when the token carrying `act` is issued. Access only intersects.

## Three domains, three tables, one decision

Rights are kept where they are granted, in the `authz` schema — four tables, each keyed on its columns, no
surrogate key, no row-level security (the module has no compartment: the space is a column of the key):

| Domain | Table | Key |
| --- | --- | --- |
| persons in a compartment | `memberships`, `user_grants` | `(subject, compartment)`, `(subject, compartment, operation)` |
| operators of the platform | `admin_grants` | `(subject, operation)`, the shared space implicit |
| clients | `client_grants` | `(subject, space, operation)`, the space a compartment or `_shared` |

A subject is the value of an `Actor`: the `sub` of a person — the GUID `Stellaegis.Identity` issues, in its `N`
form —, the `client_id` of a client, or a `job:` actor. The operation is its identity, `target.name`, the one the
module declared and labelled its route with: what Primitives calls an operation is what a rights catalogue calls a
permission.

A client of a compartment is named after it, `c_{compartment}_{name}`, and holds rights in that compartment only;
a client of the platform holds them in the shared space. The decision needs neither convention: a client is admitted
where it holds a right, and nowhere else.

### Where the space of a decision comes from

| The route | The space of the decision |
| --- | --- |
| reads the request context (`RequireRequestContext()`) | `IRequestContext.Space`: the compartment of the request, or the shared space when the group is marked `InSharedSpace()` |
| belongs to a module without compartments, and reads no context | the shared space: the accounts, the issuer, Access itself are administered from the platform |
| acts on spaces (`ActsOnSpaces()`), the compartment named in its path | the shared space: the compartment is the object of the route, not the space of the decision — purging a compartment is an act of administration |

So an operator of the platform purges a compartment from the shared space, and a member of that compartment holding
`items.purge` there does not. And an operator who is also a member of a compartment is, inside that compartment,
exactly in the situation of any member: the rights of the shared space grant nothing in a compartment.

### Where the actor comes from

From the request context when the route reads one — the host chooses the claim —, from the principal otherwise:
`client_id` makes a client, `sub` then `NameIdentifier` a person; the kind is named from the principal, not guessed
from the form of the value. The client acting on the actor's behalf is the `Actor.Via` of the context, or the
`act` claim.

⚠ The default actor source of Primitives reads `sub` and `NameIdentifier` only. A host that serves clients on
compartmented routes declares a source that reads `client_id` too, as the example does:

```csharp
builder.Services.AddStellaegisRequestContext(c => c.ActorFrom(http =>
    http.User.FindFirst("sub")?.Value ?? http.User.FindFirst("client_id")?.Value is { } v ? Actor.Parse(v) : null));
```

## Declare, install, grant, project, decide

Access is the last link of a chain whose other links are elsewhere:

1. **Declare**, in code. Each module declares its operations, `ModuleDeclaration.…WithOperations(...)`, and labels
   its routes with them. The application's own modules do the same.
2. **Install.** The installation of the host enumerates the modules and records the catalogue of their operations,
   and the default roles of the application, in the registry. No seeder.
3. **Grant.** The administrators create roles, assign them, and assign memberships, through the routes of the
   registry — the platform's for the shared space, the compartment's for a compartment.
4. **Project.** The registry computes the effective rights of a subject in a space and sends the snapshot to
   Access. Access writes it flat: it knows the result, never the roles.
5. **Decide.** The route carries its operation, the handler reads the actor and the space, Access answers.

The registry is `Stellaegis.Registry`. Until it is there, the example's `install` verb projects the snapshots
itself.

## The projection

Snapshots, never deltas. `IAccessProjection` is what a registry calls, in process or through the projection routes:

| | What it says |
| --- | --- |
| `ReplaceMembershipsAsync(subject, compartments)` | the compartments the person is admitted in; empty removes them all |
| `ReplaceUserGrantsAsync(subject, compartment, operations)` | the operations the person holds in the compartment |
| `ReplaceAdminGrantsAsync(subject, operations)` | the operations the person holds in the shared space |
| `ReplaceClientGrantsAsync(clientId, space, operations)` | the operations the client holds in the space |
| `RemoveCompartmentAsync(compartment)` | the compartment is terminated: every membership and right in it is gone |
| `RemoveSubjectAsync(subject)` | the subject is gone: every membership and right of it, everywhere |

Each replacement is one transaction, and each publishes an event through the `IEventPublisher` of Primitives —
`GrantsChangedEvent`, `MembershipsChangedEvent`, `CompartmentRemovedEvent`, `SubjectRemovedEvent` — so that the
caches of resource servers apart drop what changed. The module subscribes to nothing: the host connects its bus.
An operation identity that does not follow the grammar `target.name` is refused before it is stored.

## Installing, then verifying

Two executants, two roles, as everywhere in the family: an installer installs, under a role that owns the schema;
the application verifies at every startup, under its own role, which was granted its data rights and never a DDL
right.

```csharp
await DatabaseLifecycle.InstallAsync([AccessDatabase.Value], frame, openAsAdministrator, applicationRole: "myapp_app");
```

The installation creates the schema, applies its scripts once and records each with its checksum, and grants the
application's role what it needs. The application then verifies — `AddStellaegisDatabaseVerification()` in a
generic host, `app.Services.VerifyStellaegisDatabasesAsync()` in an ASP.NET Core host — and refuses to start when
the schema is not installed, or not as the package declares it.

## The routes of Access

Under `MapAccessDecisionEndpoints`, what a resource server apart calls, each labelled `decisions.read`:

| Route | Body | Answer |
| --- | --- | --- |
| `POST access/users/decide` | `{ subject, compartment, operation, via? }` | 204 yes; 403 with the code; 400 `Decision.Invalid` |
| `POST access/admins/decide` | `{ subject, operation, via? }` | idem |
| `POST access/clients/decide` | `{ subject, space, operation, via? }` — `space` a compartment or `_shared` | idem |

Under `MapAccessProjectionEndpoints`, what a registry in another process writes through, `grants.write` for the
writes, `grants.read` for the reads: `PUT`/`GET access/memberships/{subject}`, `access/user-grants/{subject}/{compartment}`,
`access/admin-grants/{subject}`, `access/client-grants/{subject}/{space}`; `DELETE access/compartments/{compartment}`,
`access/subjects/persons/{subject}`, `access/subjects/clients/{subject}`.

Both groups belong to the module and read no request context: the host grants `decisions.read` to the clients of
its resource servers and `grants.write` to the client of its registry, in the shared space — Access decides who may
ask Access —, and maps them on a private network, called machine to machine.

## A resource server apart

A host without Access in process takes `Stellaegis.Access.Client`, and the same `AddStellaegisAccessPolicies()`:

```csharp
builder.Services.AddStellaegisAccessClient(builder.Configuration);   // Stellaegis:Access:Client, Stellaegis:Access:Cache
builder.Services.AddSingleton<IAccessClientTokenProvider, MyM2MToken>();   // the bearer it presents to the decision routes
builder.Services.AddStellaegisAccessPolicies();
```

`HttpAccessDecision` implements `IAccessDecision` over the three decision routes, choosing the route by the kind of
the subject and the space. Behind it, `AccessDecisionCache` keeps the yes only — a right granted is seen at once, a
refusal is never kept — for `Stellaegis:Access:Cache:Expiration`, five minutes unless set, under the segments of
the space and of the subjects. The host invalidates from its subscriptions to the events of Access:

```csharp
bus.Subscribe<GrantsChangedEvent>((e, _) => { cache.Invalidate(e); return Task.CompletedTask; });
```

## Options

| Section | Options |
| --- | --- |
| `Stellaegis:Access:PostgreSQL` | `ConnectionString`, under the application's role |
| `Stellaegis:Access:Client` | `BaseUrl` — the address the decision routes are mapped under —, `Timeout` |
| `Stellaegis:Access:Cache` | `Enabled`, `Expiration`, `SizeLimit` |

Every option is validated when the host starts.

## Tests

`tests/Stellaegis.Access.Tests`: each step of the decision in memory, persons and clients, compartments and the
shared space, the intersection by `Via`, the snapshots and the removals and their events; the four stores against
PostgreSQL under an ordinary role, with the installation and the verification, the `CHECK`s of the schema, a
snapshot refused leaving the previous rights in place; the policies on the routes of a host — a member with and
without the right, a non-member, a client in its space and in another, a person through a client —, a route of a
module without compartments and a route acting on spaces decided in the shared space, the decision and projection
routes under their operations, a resource server apart on the test server and its cache invalidated.

The PostgreSQL tests take the server named by `STELLAEGIS_TEST_POSTGRESQL`, and are skipped without it; the
publishing workflow runs them against a container, and fails on any test skipped.

## License

MIT — see [LICENSE](LICENSE). Third-party notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
