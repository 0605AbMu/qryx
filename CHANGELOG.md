# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). All packages share one version line; a release publishes only the packages whose source changed since the previous tag.

## [Unreleased]

## [2.1.0] - 2026-11-04

Published packages: `Qryx.Core`, `Qryx.Expressions`, `Qryx.EntityFramework`, `Qryx.AspNetCore`, `Qryx.Swashbuckle`. `Qryx.OpenApi` and `Qryx.SourceGenerator` are unchanged and stay at 2.0.0 (they work with 2.1.0).

### Added

- **.NET 8 support**: `Qryx.Core`, `Qryx.Expressions`, `Qryx.EntityFramework` (EF Core 8), `Qryx.AspNetCore` and `Qryx.Swashbuckle` now target `net8.0` as well as `net10.0`. `Qryx.OpenApi` stays `net10.0` only (its `Microsoft.AspNetCore.OpenApi` transformers need .NET 9+). On .NET 8 the keyset cursor encoding uses Microsoft's `Microsoft.Bcl.Memory` package (`Base64Url`). CI runs the whole test suite on both runtimes.
- `LICENSE` (MIT), English `README.md` (the Uzbek version moved to `README.uz.md`), `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`, this changelog, and issue / pull request templates.

## [2.0.0] - 2026-11-04

### ⚠ Breaking changes

- **Paging response**: `PagedResult<T>` is now `{ items, hasNext, nextCursor?, total? }`. `Page`, `PageSize`, `TotalCount`, `TotalPages` and `HasPrevious` were removed; `NextCursor` is a `string`.
- **Total is opt-in**: `COUNT(*)` runs only with `?includeTotal=true`. `IncludeTotalCount` was replaced by `IncludeTotal` (default `false`).
- **Keyset cursors follow `sortBy`**: `CursorProperty` was removed. Cursors are derived from the sort plan (+ primary-key tie-breaker) and are opaque base64url tokens. `PaginationBuilder` keyset APIs take a `QryxSortPlan` or `sortBy` instead of a cursor property.
- **Client errors are HTTP 400**: invalid values, tampered or foreign cursors, pages deeper than `MaxOffset` and oversized `In` lists throw `QryxRequestException`, returned as `ValidationProblemDetails` instead of a 500 or a silently wrong result.

### Added

- `[NoTotal]` attribute: a DTO for a huge table never runs `COUNT(*)`; `includeTotal` is hidden from its API docs.
- `Op.Between` with `Range<T>` (`?age=20..35`, `from..`, `..to`).
- PostgreSQL full-text search (`Op.Fts` → `websearch_to_tsquery`) and jsonb operators (`Op.JsonContains` → `@>` / `?`).
- `nextCursor` on every page that has a next page, offset pages included, so clients can start with `?page=1` and continue with `?afterCursor=`.
- `QryxOptions.Global` and the new `QryxOptions.MaxOffset` option; `AddQryx(o => ...)` registers the options in DI and as `QryxOptions.Global`.
- Source generator diagnostics `QRYX001` (target property not found) and `QRYX002` (operator incompatible with the property type).
- MVC binder support for any `IParsable<T>` (`Range<T>`, `DateOnly`, `Guid`, …), nullable types and comma-separated `QryxList<T>`.
- Swagger / OpenAPI: every Qryx parameter is documented as camelCase and optional with the correct schema (`Range<T>` as `from..to`, `QryxList<T>` as a comma-separated array, enums as names), `sortBy` lists the sortable fields and `pageSize` shows `MaxPageSize`.
- Benchmarks: interleaved rounds with semantic (ID-set) verification, plus "new values per request" (`--unique`) and Npgsql Auto Prepare (`--prepare`) suites.
- 487 tests across core, EF Core, ASP.NET Core, source generator and API docs; CI runs all of them before packing.

### Changed

- **Parameterized SQL**: every filter value, `In` list, `ILIKE` pattern and cursor value is a SQL parameter instead of an inlined literal, so EF Core reuses compiled queries and the database reuses plans (~30x faster than 1.x with varying values, ~4x faster than Gridify on real traffic).
- **One query per page**: `Take(pageSize + 1)` yields items, `hasNext` and `nextCursor` in one roundtrip; on the last offset page `total` is computed without `COUNT(*)`.
- The projection variant selects the sort-key columns in the same `SELECT`, so it returns `nextCursor` too.
- Source generator rewritten as an incremental generator: unrelated edits no longer regenerate code, and `ApplyGenerated` builds one predicate from a static per-DTO plan (as fast as the dynamic engine instead of 1.9x slower).
- Per-(entity, DTO) filter plans with pre-built member nodes; PostgreSQL detection via compiled field readers; direct `Type.GetType` lookups instead of scanning loaded assemblies.
- Streaming (`ToPagedAsyncEnumerable`) honours its `CancellationToken` and shares the page builder with the buffered path.

### Fixed

- Array filter properties (`T[]`) no longer throw `InvalidCastException`.
- Comma-separated values for non-`[Flags]` enums are rejected instead of being read as a flags combination.
- A blank `?afterCursor=` no longer switches to keyset mode.
- Minimal APIs: binding failures and case-sensitive enum names return a problem response naming the query key.
- Source generator: DTOs in the global namespace, nested and generic DTOs generate valid code.

### Security

- `ILIKE` patterns escape user-supplied `%` and `_` with an explicit `ESCAPE '\'`.
- `MaxOffset` guard (default 10,000) against deep-`OFFSET` scans.
- `sortBy` limited to 256 characters / 5 clauses; sort and cursor keys are checked against the `[Sortable]` whitelist before any metadata lookup, and unknown names are not cached (bounded caches).
- Cursors carry a sort fingerprint; tampered cursors or cursors created for another `sortBy` are rejected.

## [1.0.1] - 2026-11-01

### Changed

- Provider detection cache uses `ConditionalWeakTable`; zero-reflection factories and cached keyset metadata on the hot path.

## [1.0.0] - 2026-11-01

### Added

- Initial release of the seven packages: `Qryx.Core`, `Qryx.Expressions`, `Qryx.EntityFramework`, `Qryx.AspNetCore`, `Qryx.SourceGenerator`, `Qryx.OpenApi`, `Qryx.Swashbuckle`.
- DTO-driven filtering with `[Filter]`, prefix conventions (`minAge` / `maxAge`), multi-target and navigation properties.
- Property-level `[Sortable]` whitelist, multi-column sorting and deterministic primary-key ordering.
- Offset and keyset pagination, single-query paging, `ToPagedResultAsync` and zero-copy `ToPagedAsyncEnumerable`.
- Database-aware case-insensitive matching (native `ILIKE` on PostgreSQL, `UPPER()` elsewhere).
- Minimal API and MVC model binding, Swashbuckle and .NET 10 OpenAPI integration.
- Optional `TagWith` query tagging, `In`-list size limit, Roslyn source generator, sample API and benchmark suite.

[Unreleased]: https://github.com/0605AbMu/qryx/compare/2.1.0...HEAD
[2.1.0]: https://github.com/0605AbMu/qryx/compare/2.0.0...2.1.0
[2.0.0]: https://github.com/0605AbMu/qryx/compare/1.0.1...2.0.0
[1.0.1]: https://github.com/0605AbMu/qryx/compare/1.0.0...1.0.1
[1.0.0]: https://github.com/0605AbMu/qryx/releases/tag/1.0.0
