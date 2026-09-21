# Changelog

Every push to `main` publishes a new patch version automatically (see `version.json` — height-based,
not manually tagged), so not every version number gets its own entry here. This file tracks what
actually changed.

## Unreleased

- Performance: added a fair "wrapped engine" baseline (deep DTO model, source-generated System.Text.Json) to the benchmarks and an in-repo HTTP host for it (`tst/RuleEngine.Wrapped`, `tst/RuleEngine.WrappedHost`, not shipped); `docs/PERFORMANCE.md` and the README now report engine vs wrapped in-process and over HTTP, including where the engine loses. Corrected earlier single-event figures that used a non-matching event.
- Added BenchmarkDotNet project `tst/RuleEngine.Benchmarks`, measured HTTP throughput, and `docs/PERFORMANCE.md` with a Performance section in the README.
- Initial release: extracted from the `plat-templates` service of the lode platform. Same v2 HTTP API, same ETag/`If-Match`
  semantics and, at default settings, the same Redis key shape (`{ENVIRONMENT}:plat-templates:{id}-templates…`), so moving
  from `plat-templates` is a URL change only.
- v1 API removed (lode stopped using it before extraction).
- New optional settings: `Templates__KeyPrefix`, `Templates__TenantKeyFormat`, `Templates__ChangedByHeader`.
- Requires `ConnectionStrings__Redis`; no Azure App Configuration, credentials or `Orange.*` libraries.
