-- ============================================================================
-- Stellaegis.Access — the rights schema on SQL Server, script 0001
-- ============================================================================
-- Applied by the installation of the application (DatabaseLifecycle.InstallAsync of
-- Stellaegis.Primitives.Data), under a login that owns the schema; the schema is created by the
-- installation before this script. The application connects as a member of the database role the
-- installation grants its data rights. The record of the scripts applied is the installation's,
-- authz._schema_migrations, not this script's.
--
-- The same four tables as the PostgreSQL script of Stellaegis.Access.PostgreSQL, the same names, the
-- same rules: all projections, written by snapshot by a registry and read by the decision; no role,
-- no catalogue, no foreign key, no row-level security. What differs, and why:
--   * every identifier — a subject, a compartment, a space, an operation — carries a binary
--     collation: a database of SQL Server is usually case-insensitive, and the decision compares them
--     exactly, as PostgreSQL does;
--   * the grammars PostgreSQL checks by a regular expression are checked here by LIKE patterns under
--     that binary collation, an underscore written [_], since it is a wildcard;
--   * every table is stored in the order of its key, clustered — the key is the decision's lookup —;
--     none is a heap.
--
-- No surrogate key: nothing joins these tables, and the natural key is the decision's lookup.
-- All constraints explicitly named.
-- ============================================================================

-- ── Memberships ──────────────────────────────────────────────────────────────
-- Which compartments a person is admitted in. The first step of a decision in a compartment: without
-- a row here, a person has nothing there, whatever rights were projected.

create table authz.memberships (
  subject      nvarchar(128) collate Latin1_General_100_BIN2 not null,
  compartment  nvarchar(63)  collate Latin1_General_100_BIN2 not null,

  constraint pk_memberships primary key clustered (subject, compartment),
  constraint ck_memberships_subject check (
    len(subject) between 1 and 128 and datalength(subject) = 2 * len(subject) and subject not like N' %'),
  constraint ck_memberships_compartment check (
    len(compartment) between 3 and 63 and compartment like N'[a-z]%' and compartment not like N'%[^a-z0-9-]%'
    and compartment not like N'%-' and compartment not like N'%--%')
);

-- A terminated compartment takes its memberships with it.
create index ix_memberships_compartment on authz.memberships (compartment);

-- ── Rights of persons in a compartment ───────────────────────────────────────
-- The second step of a decision in a compartment, for a person; the right says whether the session
-- must have proven a second factor, as the registry projects it from the operation.

create table authz.user_grants (
  subject                 nvarchar(128) collate Latin1_General_100_BIN2 not null,
  compartment             nvarchar(63)  collate Latin1_General_100_BIN2 not null,
  operation               nvarchar(127) collate Latin1_General_100_BIN2 not null,
  requires_second_factor  bit           not null,

  constraint pk_user_grants primary key clustered (subject, compartment, operation),
  constraint ck_user_grants_subject check (
    len(subject) between 1 and 128 and datalength(subject) = 2 * len(subject) and subject not like N' %'),
  constraint ck_user_grants_compartment check (
    len(compartment) between 3 and 63 and compartment like N'[a-z]%' and compartment not like N'%[^a-z0-9-]%'
    and compartment not like N'%-' and compartment not like N'%--%'),
  constraint ck_user_grants_operation check (
    operation like N'[a-z]%.[a-z]%' and operation not like N'%.%.%' and operation not like N'%[^a-z0-9_.]%'
    and operation not like N'%[_]' and operation not like N'%[_].%' and operation not like N'%[_][_]%'
    and charindex(N'.', operation) between 2 and 64 and len(operation) - charindex(N'.', operation) between 1 and 63)
);

-- A terminated compartment takes its rights with it.
create index ix_user_grants_compartment on authz.user_grants (compartment);

-- ── Rights of the operators of the platform ──────────────────────────────────
-- The shared space, implicit: a person with any row here is an operator of the platform, admitted in
-- the shared space. Being an operator admits nobody in a compartment.

create table authz.admin_grants (
  subject                 nvarchar(128) collate Latin1_General_100_BIN2 not null,
  operation               nvarchar(127) collate Latin1_General_100_BIN2 not null,
  requires_second_factor  bit           not null,

  constraint pk_admin_grants primary key clustered (subject, operation),
  constraint ck_admin_grants_subject check (
    len(subject) between 1 and 128 and datalength(subject) = 2 * len(subject) and subject not like N' %'),
  constraint ck_admin_grants_operation check (
    operation like N'[a-z]%.[a-z]%' and operation not like N'%.%.%' and operation not like N'%[^a-z0-9_.]%'
    and operation not like N'%[_]' and operation not like N'%[_].%' and operation not like N'%[_][_]%'
    and charindex(N'.', operation) between 2 and 64 and len(operation) - charindex(N'.', operation) between 1 and 63)
);

-- ── Rights of clients ────────────────────────────────────────────────────────
-- A client holds rights in a space: a compartment by its value, or the shared space by its segment,
-- _shared. A client with any row in a space is admitted in it. A client has no second factor: its
-- rights are operations.

create table authz.client_grants (
  subject    nvarchar(128) collate Latin1_General_100_BIN2 not null,
  space      nvarchar(63)  collate Latin1_General_100_BIN2 not null,
  operation  nvarchar(127) collate Latin1_General_100_BIN2 not null,

  constraint pk_client_grants primary key clustered (subject, space, operation),
  constraint ck_client_grants_subject check (
    len(subject) between 1 and 128 and datalength(subject) = 2 * len(subject) and subject not like N' %'),
  constraint ck_client_grants_space check (
    space = N'_shared'
    or (len(space) between 3 and 63 and space like N'[a-z]%' and space not like N'%[^a-z0-9-]%'
        and space not like N'%-' and space not like N'%--%')),
  constraint ck_client_grants_operation check (
    operation like N'[a-z]%.[a-z]%' and operation not like N'%.%.%' and operation not like N'%[^a-z0-9_.]%'
    and operation not like N'%[_]' and operation not like N'%[_].%' and operation not like N'%[_][_]%'
    and charindex(N'.', operation) between 2 and 64 and len(operation) - charindex(N'.', operation) between 1 and 63)
);

-- A terminated compartment takes the rights of its clients with it.
create index ix_client_grants_space on authz.client_grants (space);
