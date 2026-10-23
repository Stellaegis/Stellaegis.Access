# The example host

What an application assembled on `Stellaegis.Access` looks like: an enterprise host — one compartment, the company
`acme`, and the shared space of the platform — whose rights are Access, in process on PostgreSQL. It has a module of
its own, the inventory, with three routes under their operations; it maps the decision and projection routes of
Access as a server apart would call them; and it mints its own demonstration tokens, so that everything plays with
`curl`. One project, `Stellaegis.Access.Sample`, port 7121.

The tokens are the one thing here that is not a deployment: a real host accepts the tokens of its issuer,
`Stellaegis.Issuer`, and mints none. The rights are projected by the installation: in an application assembled in
full, `Stellaegis.Registry` computes them from its roles and memberships and projects them, in process or through
the projection routes.

## Installing

A PostgreSQL server reachable at `127.0.0.1` as `postgres` without a password — change `Sample:Install:ConnectionString`
otherwise. The installation is a verb of the host, apart from serving:

```powershell
cd samples/Stellaegis.Access.Sample
dotnet run -- install
```

It creates the database `stellaegis_access_sample` and the role `access_sample_app` if missing, installs the `authz`
schema under the administration connection, then projects the rights the example plays with, as snapshots:

| Subject | Kind | Where | Rights |
| --- | --- | --- | --- |
| `3f2504e04f8941d39a0c0305e82c3301` (`Sample:Person`) | a person | member of `acme` | `items.read`, `items.write` in `acme` |
| `9b1deb4d3b7d4bad9bdd2b0d7b3dcb6d` (`Sample:Operator`) | a person | the shared space | `items.purge` — with a second factor, as the inventory declares it —, `decisions.read`, `grants.read`, `grants.write` |
| `c_acme_robot` | a client of the company | `acme` | `items.read` |
| `registry` | the client of a registry | the shared space | `grants.read`, `grants.write` |

Run again, it changes nothing — or it puts back what the play below changed, snapshots replacing themselves.

## Running

```powershell
cd samples/Stellaegis.Access.Sample; dotnet run
```

The host verifies the `authz` schema under the application's role before serving — a schema missing stops it —
and serves `https://localhost:7121`. The HTTPS development certificate of the machine must be trusted
(`dotnet dev-certs https --trust`), or `curl` takes `-k`.

## Playing

A token for each actor, from the demonstration route — `subject` for a person, `clientId` for a client, `act` for
the client acting on the person's behalf, `amr` for the methods the person's session proved, as an issuer writes them
from the sign-in:

```powershell
$base = "https://localhost:7121"
$person   = (curl.exe -sk -X POST "$base/demo/token?subject=3f2504e04f8941d39a0c0305e82c3301" | ConvertFrom-Json).access_token
$operator = (curl.exe -sk -X POST "$base/demo/token?subject=9b1deb4d3b7d4bad9bdd2b0d7b3dcb6d&amr=pwd" | ConvertFrom-Json).access_token
$operatorMfa = (curl.exe -sk -X POST "$base/demo/token?subject=9b1deb4d3b7d4bad9bdd2b0d7b3dcb6d&amr=pwd,otp,mfa" | ConvertFrom-Json).access_token
$robot    = (curl.exe -sk -X POST "$base/demo/token?clientId=c_acme_robot" | ConvertFrom-Json).access_token
$viaRobot = (curl.exe -sk -X POST "$base/demo/token?subject=3f2504e04f8941d39a0c0305e82c3301&act=c_acme_robot" | ConvertFrom-Json).access_token
$registry = (curl.exe -sk -X POST "$base/demo/token?clientId=registry" | ConvertFrom-Json).access_token
```

Then the decisions, each the steps of Access — admitted in the space, holds the operation, proved a second factor
when the right requires one, the client acting on one's behalf holds the operation too:

```powershell
# The person reads and writes the inventory of the company (200, 201), and does not purge it: purging acts ON a
# compartment and is decided in the shared space, where the person is nobody (403, Access.NotMember).
curl.exe -sk "$base/api/items" -H "Authorization: Bearer $person"
curl.exe -sk -X POST "$base/api/items" -H "Authorization: Bearer $person" -H "Content-Type: application/json" -d '{"name":"bolt"}'
curl.exe -sk -X DELETE "$base/api/admin/inventory/compartments/acme" -H "Authorization: Bearer $person" -w "%{http_code}"

# The operator does not purge with a password alone: the right requires a second factor (403,
# Access.SecondFactorRequired). Signed in with one, the operator purges the company (200) — and does not read its
# inventory: an operator of the platform is nobody in a compartment they are not a member of (403, Access.NotMember).
curl.exe -sk -X DELETE "$base/api/admin/inventory/compartments/acme" -H "Authorization: Bearer $operator"
curl.exe -sk -X DELETE "$base/api/admin/inventory/compartments/acme" -H "Authorization: Bearer $operatorMfa"
curl.exe -sk "$base/api/items" -H "Authorization: Bearer $operatorMfa" -w "%{http_code}"

# The robot reads (200) and does not write (403, Access.Denied).
curl.exe -sk "$base/api/items" -H "Authorization: Bearer $robot"
curl.exe -sk -X POST "$base/api/items" -H "Authorization: Bearer $robot" -H "Content-Type: application/json" -d '{"name":"nut"}' -w "%{http_code}"

# The person acting through the robot reads (200), and does not write although the person may: the robot may not,
# and the rights of a person are restricted by those of the client acting for them (403, Access.ViaDenied).
curl.exe -sk "$base/api/items" -H "Authorization: Bearer $viaRobot"
curl.exe -sk -X POST "$base/api/items" -H "Authorization: Bearer $viaRobot" -H "Content-Type: application/json" -d '{"name":"nut"}' -w "%{http_code}"
```

A refusal of a policy answers 403 with problem details carrying the code of the decision — `Access.NotMember`,
`Access.Denied`, `Access.SecondFactorRequired`, `Access.ViaDenied` —, as the decision routes a resource server apart
calls answer it: a client tells a session to open again with a second factor from a right it does not hold.

The projection, as a registry in another process does it — a snapshot replacing the person's rights in the company,
which changes the next answer at once:

```powershell
curl.exe -sk -X PUT "$base/api/access/user-grants/3f2504e04f8941d39a0c0305e82c3301/acme" -H "Authorization: Bearer $registry" -H "Content-Type: application/json" -d '[{"operation":"items.read","requiresSecondFactor":false}]' -w "%{http_code}"
curl.exe -sk -X POST "$base/api/items" -H "Authorization: Bearer $person" -H "Content-Type: application/json" -d '{"name":"bolt"}' -w "%{http_code}"   # 403 now
curl.exe -sk "$base/api/access/user-grants/3f2504e04f8941d39a0c0305e82c3301/acme" -H "Authorization: Bearer $registry"   # [{"operation":"items.read","requiresSecondFactor":false}]
```

And a decision asked over HTTP, as a resource server apart would, saying whether the person's session proved a
second factor — the operator holds `decisions.read`, the robot does not:

```powershell
curl.exe -sk -X POST "$base/api/access/users/decide" -H "Authorization: Bearer $operator" -H "Content-Type: application/json" -d '{"subject":"3f2504e04f8941d39a0c0305e82c3301","compartment":"acme","operation":"items.write","secondFactor":false}'
curl.exe -sk -X POST "$base/api/access/users/decide" -H "Authorization: Bearer $robot" -H "Content-Type: application/json" -d '{"subject":"3f2504e04f8941d39a0c0305e82c3301","compartment":"acme","operation":"items.read"}' -w "%{http_code}"
```

`dotnet run -- install` puts the rights back.
