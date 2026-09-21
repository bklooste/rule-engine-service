# Changelog

Every push to `main` publishes a new patch version automatically (see `version.json` — height-based,
not manually tagged), so not every version number gets its own entry here. This file tracks what
actually changed.

## Unreleased

- Initial release: extracted from the `plat-templates` service of the lode platform. Same v2 HTTP API, same ETag/`If-Match`
  semantics and, at default settings, the same Redis key shape (`{ENVIRONMENT}:plat-templates:{id}-templates…`), so moving
  from `plat-templates` is a URL change only.
- v1 API removed (lode stopped using it before extraction).
- New optional settings: `Templates__KeyPrefix`, `Templates__TenantKeyFormat`, `Templates__ChangedByHeader`.
- Requires `ConnectionStrings__Redis`; no Azure App Configuration, credentials or `Orange.*` libraries.
