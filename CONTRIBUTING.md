# Contributing to Qryx

Thanks for taking the time to contribute! Bug reports, documentation fixes, translations, tests and new features are all welcome.

By participating you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
Security vulnerabilities must **not** be reported in public issues — see [SECURITY.md](SECURITY.md).

## Ways to contribute

- **Report a bug**: open an issue using the *Bug report* template. A minimal DTO, the request URL and the generated SQL make bugs much faster to fix.
- **Suggest a feature**: open an issue using the *Feature request* template before writing a large change, so we can agree on the design first.
- **Improve the docs**: the developer guide ([docs/USAGE.md](docs/USAGE.md)) is currently in Uzbek; English translations of its sections are especially valuable.
- **Send a pull request**: small, focused PRs are reviewed fastest.

## Development setup

Requirements:

- [.NET 10 SDK](https://dotnet.microsoft.com/download) and the .NET 8 runtime (the libraries and tests target `net8.0` and `net10.0`; `dotnet test` runs both)
- Optional: Docker, for the PostgreSQL benchmarks and provider-specific checks

```bash
git clone https://github.com/0605AbMu/qryx.git
cd qryx

dotnet build Qryx.slnx
dotnet test Qryx.slnx
```

The test suite needs no database server: it runs on EF Core InMemory, and PostgreSQL translation is checked by generating SQL (`ToQueryString()`) with the Npgsql provider offline.

### PostgreSQL (optional)

The PostgreSQL benchmark suites connect to `localhost:5432` (see `benchmarks/Qryx.Benchmarks/Services/PostgresBenchmarkHelper.cs` for the connection string). A disposable instance:

```bash
docker run -d --name qryx-pg -p 5432:5432 -e POSTGRES_USER=taxi -e POSTGRES_PASSWORD=taxi -e POSTGRES_DB=qryx_benchmark postgres:17-alpine
```

```bash
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --fast
```

## Repository layout

| Path | Contents |
| :--- | :--- |
| `src/Qryx.Core` | Contracts and attributes (no dependencies) |
| `src/Qryx.Expressions` | Expression tree builders, sort plans, keyset cursors, caches |
| `src/Qryx.EntityFramework` | EF Core extensions and `PagedResult<T>` |
| `src/Qryx.AspNetCore` | Model binding, Minimal API / MVC integration, HTTP 400 mapping |
| `src/Qryx.SourceGenerator` | Roslyn incremental generator (`netstandard2.0`) |
| `src/Qryx.OpenApi`, `src/Qryx.Swashbuckle` | API docs integrations (shared code in `src/Qryx.ApiDocs.Shared`) |
| `tests/` | xUnit test projects, one per area |
| `benchmarks/` | Fast diagnostic runner and BenchmarkDotNet suites |
| `samples/Qryx.SampleApi` | Runnable Minimal API sample |

## Coding guidelines

- Follow the existing style: file-scoped namespaces, nullable reference types enabled, `var` where the type is obvious.
- Public APIs need English XML documentation comments.
- Do not introduce new compiler warnings.
- **Security invariants**: every user-supplied value must reach SQL as a parameter (never a literal), and every new input must be bounded (length, count or depth). Invalid client input throws `QryxRequestException` (HTTP 400), never a 500.
- **Performance**: the per-request hot path must not use reflection or build new generic methods after the first request. If a change touches the hot path, run the `--fast` benchmarks before and after and include the numbers in the PR.
- Keep the source generator and the dynamic engine behaviourally identical; add tests for both when changing filter semantics.

## Tests

- Every bug fix needs a regression test; every feature needs tests covering the happy path and invalid input.
- Run the whole solution before pushing: `dotnet test Qryx.slnx`.

## Documentation

- User-visible changes must update [README.md](README.md) **and** [README.uz.md](README.uz.md), and [docs/USAGE.md](docs/USAGE.md) where relevant.
- `README.md` is packed into the NuGet packages, so links in it must be absolute GitHub URLs.
- Add an entry to the `Unreleased` section of [CHANGELOG.md](CHANGELOG.md).

## Commit messages

We use [Conventional Commits](https://www.conventionalcommits.org/) with the package as scope:

```text
feat(expressions): add Op.NotIn
fix(aspnetcore): return 400 for malformed enum values
docs: translate the paging section of USAGE.md
feat(efcore)!: rename PagedResult.Total   <- "!" marks a breaking change
```

Common types: `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `bench`, `ci`, `chore`.

## Pull request process

1. Fork the repository and create a branch from `master`.
2. Make your change with tests and docs, and make sure `dotnet build` and `dotnet test` pass.
3. Open a pull request and fill in the template.
4. A maintainer reviews it; please keep the branch up to date while the review is in progress.

## Releases (maintainers)

Versions follow [Semantic Versioning](https://semver.org/). Pushing a tag in `X.Y.Z` form triggers `.github/workflows/deploy.yml`, which runs every test project, packs the changed packages and publishes them to NuGet.org and GitHub Packages. Move the `Unreleased` CHANGELOG entries under the new version before tagging.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
