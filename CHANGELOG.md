# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [5.0.0] — 2026-10-21

First release of the packages: the authorization of the Stellaegis framework, extracted and rewritten on
`Stellaegis.Primitives` 5.1 as a decision on an actor, a space and an operation. They start at the major version
of the family. Access decides, and does nothing else: it knows neither accounts, nor tokens, nor roles, nor a
catalogue, and stops at the rights of calling.

### What it keeps from the framework

The decision point as a service of its own, in process or apart; effective rights projected flat by the registry,
never computed here; the three domains of the framework — the users of a tenant, the administrators of the
platform, the applications — each with its table and its decision route; the cache of decisions of a resource
server apart, five minutes, invalidated by segments.

### What changes for a host coming from the framework

1. **One question, three steps.** `IAccessDecision.DecideAsync(actor, space, operation)` replaces
   `IPermissionEvaluator.EvaluateUserAsync` and `EvaluateAdminAsync`: admitted in the space, holds the operation,
   the client acting on one's behalf holds it too. The answer is a `Result` with a code — `Access.NotMember`,
   `Access.Denied`, `Access.ViaDenied`.
2. **No policy prefix.** `user:`, `app:` and `admin:` are gone: a policy is named after an operation, `items.read`,
   and the domain is read from the request — a person in a compartment, a person in the shared space, a client.
   `AddStellaegisAccessPolicies()` replaces `AddStellaegisAuthorizationPolicies()`, and answers the policies
   `RequireAuthorizationPerOperation` of Primitives names.
3. **No `apiCode`.** `[StellaegisApiCode<TApi>]`, `IStellaegisApi` and `ApiCodeMetadataValidator` are gone: the
   operation carries its target, and the module its name.
4. **No expression.** `PermissionExpression`, its `*` and `+`, are gone: a route carries one operation; a
   combination is one more operation.
5. **The space is part of the key.** `tenant_slug` is replaced by the compartment, and a client's rights are keyed
   by the space it holds them in: a client of one compartment holds nothing in another, which the framework's
   evaluation of applications did not check.
6. **Membership is a projection, not a synthetic permission.** `TenantMembership`'s atom `tenant/enter` is replaced
   by the `memberships` table, projected by the registry; `AddStellaegisTenantIsolation` and its fallback policy,
   `TenantMembershipRequirement`, `TenantAnonymousRouteGuard` are gone — the startup check of Primitives holds the
   routes, and the decision fails closed without a membership.
7. **Four tables, one schema `authz`**: `memberships`, `user_grants`, `admin_grants`, `client_grants`, keyed on
   their columns, installed by `DatabaseLifecycle.InstallAsync([AccessDatabase.Value], …)` under an administration
   role and verified at every startup under the application's role, rather than applied by hand from `docs/sql`.
8. **Snapshots through `IAccessProjection`**, each one transaction, each publishing an `IEvent` —
   `GrantsChangedEvent`, `MembershipsChangedEvent`, `CompartmentRemovedEvent`, `SubjectRemovedEvent` — through
   `IEventPublisher`; the module subscribes to nothing, the host connects its bus. The `SyncAll…` of
   `IAuthorizationStore` and `UseStellaegisAuthorizationBackplane` are gone.
9. **A client acting on a person's behalf** is decided: the `Actor.Via` of Primitives, or the `act` claim of a token
   exchange, must hold the operation too. Whether the client may act for persons at all is the issuer's attribute,
   not a right here.
10. **No bootstrap seeder.** `AccessBootstrapSeeder`, which wrote the first administrator's rights straight into
    the store, is gone: the installation projects them, as the example's `install` verb does.
11. **`Stellaegis.Access.Client` replaces `Authorization.Client` and `Authorization.InProcess`**: one port,
    `IAccessDecision`, implemented over HTTP behind `AccessDecisionCache`; in process, the core is the decision and
    needs no client. `Stellaegis:ExternalServices:Access` becomes `Stellaegis:Access:Client`,
    `Stellaegis:Authorization:Cache` becomes `Stellaegis:Access:Cache`.
12. **The catalogue constants are gone.** `AdminPermissions`, `TenancyPermissions`, `TenancyRoleTemplates`, which
    the framework kept in `Authorization` for modules that were not it, belong to the modules that declare the
    operations and to the registry that holds the roles.
13. **`CurrentUser` is not taken over.** `ICurrentUserContext` is the actor of the request context of Primitives;
    the registry of user links per tenant database belongs to the platform.
