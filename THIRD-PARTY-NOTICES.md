# Third-party notices

Stellaegis.Access is licensed under the MIT License (see [LICENSE](LICENSE)).

Its packages contain no third-party code. They reference the packages below, which NuGet installs
alongside them, each under its own license.

| Component | Used by | License (SPDX) | Project |
| --- | --- | --- | --- |
| Stellaegis.Primitives, and Stellaegis.Primitives.Hosting | all packages | MIT | <https://github.com/Stellaegis/Stellaegis.Primitives> |
| Stellaegis.Primitives.Data | `Stellaegis.Access.PostgreSQL`, `Stellaegis.Access.SqlServer` | MIT | <https://github.com/Stellaegis/Stellaegis.Primitives> |
| Stellaegis.Primitives.AspNetCore | `Stellaegis.Access.AspNetCore` | MIT | <https://github.com/Stellaegis/Stellaegis.Primitives> |
| Stellaegis.Caching | `Stellaegis.Access.Client` | MIT | <https://github.com/Stellaegis/Stellaegis.Caching> |
| Dapper | `Stellaegis.Access.PostgreSQL`, `Stellaegis.Access.SqlServer` | Apache-2.0 | <https://github.com/DapperLib/Dapper> |
| Npgsql | `Stellaegis.Access.PostgreSQL` | PostgreSQL | <https://github.com/npgsql/npgsql> |
| Microsoft.Data.SqlClient | `Stellaegis.Access.SqlServer` | MIT | <https://github.com/dotnet/SqlClient> |
| System.Security.Cryptography.Pkcs | `Stellaegis.Access.SqlServer` (pinned above the version Microsoft.Data.SqlClient pulls in) | MIT | <https://github.com/dotnet/runtime> |
| Microsoft.Extensions.\* | all packages | MIT | <https://github.com/dotnet> |

`Stellaegis.Access.AspNetCore` uses the ASP.NET Core shared framework, which the host already has.

Test-only dependencies (xUnit, the ASP.NET Core test host) and the dependencies of the example host under
`samples/` (`Microsoft.AspNetCore.Authentication.JwtBearer`, Npgsql) are not part of any package and are not
listed here. The PostgreSQL and SQL Server servers that the tests and the publishing workflow use are not part
of any package either.
