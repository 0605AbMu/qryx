## Summary

<!-- What does this PR change, and why? Link the issue it closes. -->

Closes #

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change (public API or behavior changes)
- [ ] Performance
- [ ] Documentation / tests / CI only

## Checklist

- [ ] `dotnet build Qryx.slnx` passes without new warnings
- [ ] `dotnet test Qryx.slnx` passes, and new behavior is covered by tests
- [ ] User-supplied values still reach SQL as parameters, and new inputs are bounded
- [ ] Source generator and dynamic engine behave the same (if filter semantics changed)
- [ ] `README.md` and `README.uz.md` (and `docs/USAGE.md`) are updated for user-visible changes
- [ ] `CHANGELOG.md` has an entry under `Unreleased`
- [ ] Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/)

## Benchmarks (hot-path changes only)

<!-- `--fast` results before and after. -->
