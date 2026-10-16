# Changelog

Every push to `main` publishes a new patch version automatically (see `version.json` — height-based,
not manually tagged), so not every version number gets its own entry here. This file tracks what
actually changed.

## Unreleased

- History is transactional (design note `docs/design/2.1-history-groups-retention.md`, L1): every write commits
  the document, its version counter, its snapshot, the version index and a new per-version metadata hash
  (`…:vmeta`) in one `MULTI`/`EXEC`, conditional on the version it read. 2.0 wrote the snapshot in a second,
  best-effort transaction, so a fault between the two left a version with no history. Pruning stays a later,
  separate step and now also removes the version's metadata. `GET v2/templates/{id}/versions` reads the
  metadata hash instead of every snapshot (falling back to the snapshot for versions written before 2.1).
- **Behaviour change:** a `PUT v2/templates/{id}` with no `If-Match` (or `If-Match: *`) is retried when another
  write lands between its read and its commit, and returns `409` after 4 lost races in a row; 2.0 documented
  that such a write always succeeds.
- **Behaviour change:** a rollback records its caller as `changedBy` (2.0 copied the author of the restored
  version).
- `DELETE v2/templates/{id}` is conditional on the version it read and retried, so a write racing the delete
  can no longer leave an orphaned snapshot.
- README: Redis must be standalone and must not evict history keys (`noeviction` or a `volatile-*` policy).

- Performance: added a fair "wrapped engine" baseline (deep DTO model, source-generated System.Text.Json) to the benchmarks and an in-repo HTTP host for it (`tst/RuleEngine.Wrapped`, `tst/RuleEngine.WrappedHost`, not shipped); `docs/PERFORMANCE.md` and the README now report engine vs wrapped in-process and over HTTP, including where the engine loses. Corrected earlier single-event figures that used a non-matching event.
- Added BenchmarkDotNet project `tst/RuleEngine.Benchmarks`, measured HTTP throughput, and `docs/PERFORMANCE.md` with a Performance section in the README.
- Initial release: extracted from the `plat-templates` service of the lode platform. Same v2 HTTP API, same ETag/`If-Match`
  semantics and, at default settings, the same Redis key shape (`{ENVIRONMENT}:plat-templates:{id}-templates…`), so moving
  from `plat-templates` is a URL change only.
- v1 API removed (lode stopped using it before extraction).
- New optional settings: `Templates__KeyPrefix`, `Templates__TenantKeyFormat`, `Templates__ChangedByHeader`.
- Requires `ConnectionStrings__Redis`; no Azure App Configuration, credentials or `Orange.*` libraries.
