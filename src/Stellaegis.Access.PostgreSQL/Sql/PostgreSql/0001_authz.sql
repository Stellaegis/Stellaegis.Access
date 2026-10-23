-- ============================================================================
-- Stellaegis.Access — the rights schema, script 0001
-- ============================================================================
-- Applied by the installation of the application (DatabaseLifecycle.InstallAsync of
-- Stellaegis.Primitives.Data), under the role that owns the schema; the schema is created by the
-- installation before this script. The application connects under an ordinary role, granted its
-- data rights by the installation. The record of the scripts applied is the installation's,
-- authz._schema_migrations, not this script's.
--
-- Four tables, one per domain of a decision, all projections: a registry writes them by snapshot —
-- every right of a subject in a space, replaced whole — and the decision reads them. No role, no
-- catalogue, no foreign key: a right on an operation the application does not declare is useless,
-- not forbidden. No row-level security: the module has no compartment, every row belongs to the
-- application, and the space a right is granted in is a column of the key.
--
-- No surrogate key: nothing joins these tables, and the natural key is the decision's lookup.
-- All constraints explicitly named.
-- ============================================================================

-- ── Grammars ─────────────────────────────────────────────────────────────────
-- A subject is an actor of Stellaegis.Primitives: 1 to 128 characters, neither leading nor trailing
-- whitespace — a person by the sub of its token (a GUID written as 32 hex digits), a client by its
-- client_id (c_{compartment}_{name} for a client of a compartment). The same text, whatever its form.
-- A compartment follows the grammar of Compartment: 3 to 63 lowercase letters, digits and single
-- hyphens, starting with a letter and ending with a letter or a digit.
-- An operation is an identity target.name: each part 1 to 63 lowercase letters, digits and single
-- underscores, starting with a letter and ending with a letter or a digit.
-- The checks below repeat those grammars so that a projection written past the module — a script,
-- a repair by hand — cannot store what the decision would never match.

-- ── Memberships ──────────────────────────────────────────────────────────────
-- Which compartments a person is admitted in. The first step of a decision in a compartment: without
-- a row here, a person has nothing there, whatever rights were projected.

create table authz.memberships (
  subject      varchar(128) not null,
  compartment  varchar(63)  not null,

  constraint pk_memberships primary key (subject, compartment),
  constraint ck_memberships_subject check (
    char_length(subject) between 1 and 128 and subject = btrim(subject)),
  constraint ck_memberships_compartment check (
    compartment ~ '^[a-z][a-z0-9-]{1,61}[a-z0-9]$' and compartment !~ '--')
);

comment on table  authz.memberships             is 'The compartments a person is admitted in, projected by the registry. One row per (subject, compartment).';
comment on column authz.memberships.subject     is 'The person, by the sub of its token.';
comment on column authz.memberships.compartment is 'The compartment, by its value.';

-- A terminated compartment takes its memberships with it.
create index ix_memberships_compartment on authz.memberships (compartment);

-- ── Rights of persons in a compartment ───────────────────────────────────────
-- The second step of a decision in a compartment, for a person. The key is the decision's lookup:
-- (subject, compartment, operation), no other index needed to answer "does she hold it there?".

create table authz.user_grants (
  subject                 varchar(128) not null,
  compartment             varchar(63)  not null,
  operation               varchar(127) not null,
  requires_second_factor  boolean      not null,

  constraint pk_user_grants primary key (subject, compartment, operation),
  constraint ck_user_grants_subject check (
    char_length(subject) between 1 and 128 and subject = btrim(subject)),
  constraint ck_user_grants_compartment check (
    compartment ~ '^[a-z][a-z0-9-]{1,61}[a-z0-9]$' and compartment !~ '--'),
  constraint ck_user_grants_operation check (
    operation ~ '^[a-z]([a-z0-9_]{0,61}[a-z0-9])?\.[a-z]([a-z0-9_]{0,61}[a-z0-9])?$' and operation !~ '__')
);

comment on table  authz.user_grants           is 'The rights of persons in a compartment, projected by the registry from its roles. One row per (subject, compartment, operation).';
comment on column authz.user_grants.operation is 'The identity of the operation, target.name, as the module declares it.';
comment on column authz.user_grants.requires_second_factor is 'Whether the decision refuses the right to a session that did not prove a second factor: the operation''s, as the registry projects it.';

-- A terminated compartment takes its rights with it.
create index ix_user_grants_compartment on authz.user_grants (compartment);

-- ── Rights of the operators of the platform ──────────────────────────────────
-- The shared space, implicit: a person with any row here is an operator of the platform, admitted in
-- the shared space; the row says what she may do there. Being an operator admits nobody in a
-- compartment.

create table authz.admin_grants (
  subject                 varchar(128) not null,
  operation               varchar(127) not null,
  requires_second_factor  boolean      not null,

  constraint pk_admin_grants primary key (subject, operation),
  constraint ck_admin_grants_subject check (
    char_length(subject) between 1 and 128 and subject = btrim(subject)),
  constraint ck_admin_grants_operation check (
    operation ~ '^[a-z]([a-z0-9_]{0,61}[a-z0-9])?\.[a-z]([a-z0-9_]{0,61}[a-z0-9])?$' and operation !~ '__')
);

comment on table authz.admin_grants is 'The rights of the operators of the platform, in the shared space. One row per (subject, operation); any row admits the person in the shared space.';

-- ── Rights of clients ────────────────────────────────────────────────────────
-- A client — M2M, or a job — holds rights in a space: a compartment by its value, or the shared space
-- by its segment, _shared, the one projection every module of the family uses (Space.ToSegment()).
-- A client with any row in a space is admitted in it: a client of a compartment has rights in its
-- own compartment and nowhere else, a client of the platform in the shared space. A client acting
-- for a person (Actor.Via) must hold the operation here too: the decision is the intersection.

create table authz.client_grants (
  subject      varchar(128) not null,
  space        varchar(63)  not null,
  operation    varchar(127) not null,

  constraint pk_client_grants primary key (subject, space, operation),
  constraint ck_client_grants_subject check (
    char_length(subject) between 1 and 128 and subject = btrim(subject)),
  constraint ck_client_grants_space check (
    space = '_shared' or (space ~ '^[a-z][a-z0-9-]{1,61}[a-z0-9]$' and space !~ '--')),
  constraint ck_client_grants_operation check (
    operation ~ '^[a-z]([a-z0-9_]{0,61}[a-z0-9])?\.[a-z]([a-z0-9_]{0,61}[a-z0-9])?$' and operation !~ '__')
);

comment on table  authz.client_grants       is 'The rights of clients, in a compartment or in the shared space. One row per (subject, space, operation); any row admits the client in the space.';
comment on column authz.client_grants.space is 'A compartment by its value, or _shared for the shared space.';

-- A terminated compartment takes the rights of its clients with it.
create index ix_client_grants_space on authz.client_grants (space);
