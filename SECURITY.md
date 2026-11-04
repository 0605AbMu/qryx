# Security Policy

Qryx turns untrusted query strings into database queries, so we take security reports seriously.

## Supported versions

| Version | Supported |
| :--- | :--- |
| 2.x | ✅ Security fixes |
| 1.x | ❌ Please upgrade to 2.x (2.0 parameterizes every value and adds OFFSET depth and `sortBy` limits) |

## Reporting a vulnerability

**Please do not report security vulnerabilities in public GitHub issues, discussions or pull requests.**

Report them privately through GitHub's private vulnerability reporting:

👉 **[Report a vulnerability](https://github.com/0605AbMu/qryx/security/advisories/new)** (repository → *Security* tab → *Report a vulnerability*)

Please include as much of the following as you can:

- The affected package(s) and version(s) (`Qryx.Core`, `Qryx.Expressions`, `Qryx.EntityFramework`, `Qryx.AspNetCore`, `Qryx.SourceGenerator`, `Qryx.OpenApi`, `Qryx.Swashbuckle`)
- The database provider and version (e.g. PostgreSQL 17 with the Npgsql EF Core provider and its version)
- The DTO definition and the request URL (query string) that triggers the issue
- The generated SQL, if relevant (`ToQueryString()` or the EF Core log)
- The impact: what an attacker can read, change or exhaust
- A proof of concept or a failing test, if you have one

## What to expect

- **Acknowledgement** within 3 business days.
- **Initial assessment** (confirmed / not a vulnerability / need more information) within 7 days.
- For confirmed issues, a fix is developed in a private security advisory, released as a patch version, and the advisory (with a CVE where appropriate) is published together with the release.
- You will be credited in the advisory unless you prefer to stay anonymous.

Please give us a reasonable amount of time to release a fix before disclosing the issue publicly.

## Scope

In scope — issues in Qryx itself, for example:

- SQL injection or any user-supplied value reaching SQL as a literal instead of a parameter
- Filtering, sorting or cursor access to properties that are not exposed by the DTO (`[Filter]` / `[Sortable]`)
- Forged or tampered keyset cursors that bypass filters or leak data
- Denial of service through unbounded input (page size, OFFSET depth, `In` lists, `sortBy`, cache growth) that bypasses the built-in limits
- Unhandled exceptions (HTTP 500) or information disclosure from malformed input

Out of scope:

- Vulnerabilities in your application code, EF Core, database providers or other dependencies (please report those upstream)
- Applications that deliberately disable or raise the built-in limits (`QryxOptions.MaxPageSize`, `MaxOffset`)
- Missing authorization: Qryx does not decide which rows a user may see; apply tenant/ownership filters to the `IQueryable` before calling Qryx

## Hardening recommendations

- Keep `QryxOptions.MaxPageSize` and `QryxOptions.MaxOffset` at their defaults or lower, and use keyset pagination (`afterCursor`) for deep pages.
- Mark large tables with `[NoTotal]` so clients cannot trigger `COUNT(*)` scans.
- Apply authorization filters (`Where(x => x.TenantId == tenantId)`) before `ToPagedResultAsync`.
- Use `.WithQryx()` (Minimal API) or `AddQryx()` (MVC) so invalid input is returned as HTTP 400.
