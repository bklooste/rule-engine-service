# rule-engine-service

[![CI](https://github.com/bklooste/rule-engine-service/actions/workflows/ci.yml/badge.svg)](https://github.com/bklooste/rule-engine-service/actions/workflows/ci.yml)
[![Image](https://img.shields.io/badge/ghcr.io-rule--engine--service-blue)](https://github.com/bklooste/rule-engine-service/pkgs/container/rule-engine-service%2Fservice)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

A small, fast, NativeAOT rule engine over HTTP. You store **templates** — named sets of rules, each a
condition (`dataToMatch`) and a JSON fragment (`matchFragment`) — and POST a batch of JSON events at
them. Every rule whose condition matches an event contributes its fragment, and the fragments are
merged into one result per event. Templates live in Redis with `ETag`/`If-Match` optimistic
concurrency, per-version history and rollback.

Nothing in it is domain-specific: a template id is an opaque string you choose (use it as a tenant,
a product, a brand…).

## Quick start

```bash
docker compose up -d
curl -s localhost:8080/health

# 1. store a template (one rule: events with eventId=e1 get {"response":"hello"})
curl -s -X PUT localhost:8080/v2/templates/acme -H 'content-type: application/json' -d '{
  "aggregationField": "code",
  "templates": [{ "name": "r1", "dataToMatch": { "eventId": "e1" }, "matchFragment": { "response": "hello" } }]
}'
# -> {"version":1}

# 2. evaluate a batch of events against it
curl -s -X POST localhost:8080/v2/templates/acme/evaluate -H 'content-type: application/json' \
  -d '[{ "eventId": "e1" }, { "eventId": "other" }]'
```

Just the image (needs a reachable Redis):

```bash
docker run --rm -p 8080:8080 -e ConnectionStrings__Redis=host.docker.internal:6379 \
  ghcr.io/bklooste/rule-engine-service/service:latest
```

Images are public on GHCR — no pull secret. Multi-arch (`linux/amd64`, `linux/arm64`).

## Configuration

The configuration surface **is** the public API of a container. Settings come from environment
variables (ASP.NET Core `Section__Key` form), an optional mounted `appsettings.json`, or both — env wins.
Invalid configuration fails at startup with a clear message in the container log.

| Env var | Type | Default | Description |
|---|---|---|---|
| `ConnectionStrings__Redis` | string | _(required)_ | Redis connection string, e.g. `redis:6379,password=…`. The service fails to start without it. |
| `ENVIRONMENT` | string | `local` | Namespaces every Redis key (`{ENVIRONMENT}:plat-templates:…`) so several environments can share one Redis. |
| `Templates__KeyPrefix` | string | _(empty)_ | Overrides the whole key prefix. Empty = `{ENVIRONMENT}:plat-templates:`. `[A-Za-z0-9:_.-]*`. |
| `Templates__TenantKeyFormat` | string | `{tenant}-templates` | Redis key for one template id; must contain `{tenant}`. The default keeps the key shape of the `plat-templates` service this was extracted from. |
| `Templates__ChangedByHeader` | string | `userid` | Request header whose value is recorded as `changedBy` on writes. See [Identity](#identity-and-security). |
| `TemplateApi__MaxEventsPerRequest` | int | `100` | Max events in one evaluate call. |
| `TemplateApi__MaxTemplateIdsPerRequest` | int | `10` | Max template ids in one multi-template evaluate call. |
| `TemplateHistory__RetentionDays` | int | `90` | Days a per-version snapshot is kept before it is pruned on the next write. |
| `TemplateVersionCache__Duration` | int (ms) | `5000` | How long a pod trusts its last version check before re-reading Redis. A `PUT` reaches every pod within this window. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | url | _(unset)_ | Standard OpenTelemetry. Unset = nothing exported. Also honours `OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_PROTOCOL`, etc. |
| `ASPNETCORE_HTTP_PORTS` | string | `8080` | Listening port. |

**Secrets.** Any setting can be supplied from a file by adding `_FILE` to its env var, e.g.
`ConnectionStrings__Redis_FILE=/run/secrets/redis` — use this with Docker/Kubernetes secret mounts so
credentials don't appear in `docker inspect`. The service never logs its configuration.

### Identity and security

The service does **no authentication**. It expects to sit behind a gateway or proxy that authenticates
callers and forwards their identity in the `Templates__ChangedByHeader` header (default `userid`); that value is
stored as `changedBy` on each version. Do not expose the write routes directly to untrusted networks.

## API reference

All routes are under `/v2`. The full OpenAPI document is served at `/openapi/v1.json`.

| Method | Path | Description |
|---|---|---|
| GET | `/health` | `200` when healthy (includes Redis connectivity). |
| GET | `/v2/templates` | List template ids. |
| GET | `/v2/templates/{id}` | The template document. Response carries `ETag: "<version>"` and `X-Template-Version`. `404` if unknown. |
| PUT | `/v2/templates/{id}` | Create or replace. Every rule is compiled first; one that cannot compile is rejected `400` by name. Honours `If-Match`. Returns `{"version":n}`. |
| DELETE | `/v2/templates/{id}` | Delete the template and its history. |
| GET / PUT / DELETE | `/v2/templates/{id}/rules/{ruleName}` | Read, upsert or delete a single rule. PUT/DELETE honour `If-Match`. |
| GET | `/v2/templates/{id}/versions` | Version history (newest first): version, `updatedAt`, `changedBy`. |
| GET | `/v2/templates/{id}/versions/{n}` | The document as it was at version `n`. |
| POST | `/v2/templates/{id}/rollback/{n}` | Restore version `n` **as a new version** (history is never rewritten). |
| POST | `/v2/templates/{id}/evaluate` | Body: one event or an array of events. Returns the merged fragment(s). `X-Template-Version` header. |
| POST | `/v2/templates/evaluate` | Body `{"templateIds":[…],"events":[…]}` — several templates over the same events. Returns `{"versions":{id:n},"results":[{id:fragment}…]}`. |
| POST | `/v2/templates/{id}/explain` | Like evaluate for exactly one event, but reports which rules matched and why the others did not. |

### Concurrency: ETag / If-Match

A document's `ETag` is its version. Send it back as `If-Match` and a write against a version that has
moved on is refused with `412 Precondition Failed` instead of silently winning. A write with no
`If-Match` always succeeds (bulk import).

```bash
curl -si localhost:8080/v2/templates/acme | grep -i etag          # ETag: "1"
curl -s -X PUT localhost:8080/v2/templates/acme -H 'If-Match: "1"' -H 'content-type: application/json' -d @doc.json   # {"version":2}
curl -s -X PUT localhost:8080/v2/templates/acme -H 'If-Match: "1"' -H 'content-type: application/json' -d @doc.json   # 412
curl -s -X POST localhost:8080/v2/templates/acme/rollback/1                                                            # new version 3
```

### Rules

A template is `{ "aggregationField": "…", "templates": [ rule… ] }`. A rule is
`{ "name", "dataToMatch", "matchFragment" }` plus optional `group`, `tags`, and `expiresAt` (a rule past
its expiry is skipped on evaluate and read, and pruned on the next write). Conditions are conjunctive.

**`dataToMatch`** — flat form, `{ "eventProperty": condition }`, all must match (property lookup is
case-insensitive):

| Condition | Matches |
|---|---|
| `"AUS"` | equals `AUS`, ignoring case |
| `"!AUS"` | anything but `AUS` |
| `"Racing-*"`, `"*-Metro"`, `"*mid*"` | starts / ends with / contains |
| `">1.5"`, `"<10"`, `"==3"` | numeric comparison |
| `">{ago(1h)}"` | date-time comparison against now minus an offset (`ago(1h)` is one hour in the **past**) |
| `true` / `false` | boolean equality |

…or **JsonLogic** form, detected when `dataToMatch` is a single operator key: `and`, `!`, `==ci`, `==`,
`<`, `>`, `startsWith`, `endsWith`, `contains`, `some`, `required` (`or` is not supported — conditions are
conjunctive):

```json
{ "and": [ { "==ci": [{ "var": "eventId" }, "e1"] }, { "startsWith": [{ "var": "eventType" }, "AEv"] } ] }
```

By default a condition on a property the event lacks does not fail the rule; wrap it in
`{"required": [ … ]}` to make absence a non-match. The compiled JsonLogic frontend is checked against the
flat-form matcher by oracle tests (`tst/…/JsonLogicCompilerOracleTests.cs`).

**`matchFragment`** — any JSON object. The fragments of all matching rules are merged into one result.
A string value written as `"{path}"` is replaced by that (dotted) property of the event, e.g.
`{ "changedBy": "{customer.id}" }`. When two rules contribute an array under the same key, elements that
share the same `aggregationField` value are merged into one element instead of duplicated.

## Performance

The engine is designed to avoid JSON conversion and per-request rule parsing: rules are compiled once per
template version into plain condition objects (no `Expression.Compile`, so it works under NativeAOT), events are
read straight off the parsed `JsonElement` with no POCO or dictionary deserialisation, fragments are immutable and
shared, the merge is written to the response as a plan rather than a cloned tree, and compiled templates are held
in a version-checked in-memory cache. We think that makes it a good fit for a low-latency rules service, but **we
have not benchmarked other engines head to head**, and against a hand-specialised naive baseline it wins on
allocations but not on CPU time at 100 rules. Here are our numbers and how to reproduce them.

| Measurement (i7-13700KF, WSL2, .NET 10) | Result |
|---|---|
| In-process, 10 rules, 1 event: parse + match + merge + write | 1.5 us, 2.7 KB allocated |
| In-process, 100 rules, 1 event | 9.1 us, 8.3 KB allocated |
| Compile a 100-rule template (once per version) | 64 us |
| HTTP, NativeAOT container, 10 rules, 1 event, 16 connections | about 30,000 req/s, p99 1.7 ms |
| Container size / resident memory | 38 MB image, about 20-60 MiB |

Details, methodology, caveats and commands: [docs/PERFORMANCE.md](docs/PERFORMANCE.md).

## Deployment

Runs on **any Kubernetes node**; it only needs network access to Redis. Redis is an external
endpoint, never something the service ships or pins a node for.

```bash
kubectl apply -f deploy/k8s/deployment.yaml
```

The manifest deliberately has no `nodeSelector`, `affinity` or tolerations. Pin an explicit image
version (`:2.0`), not `:latest`, in production. Run several replicas freely: a template `PUT` is
visible on every pod within `TemplateVersionCache__Duration`.

## Development

```bash
dotnet build RuleEngine.slnx
dotnet test RuleEngine.slnx
docker build -t rule-engine-service .
```

Versions come from [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
(`version.json`); every push to `main` that passes tests publishes a new patch version. Bump
`version.json` for a minor/major. See [CHANGELOG.md](CHANGELOG.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

[MIT](LICENSE). Third-party licences of the shipped image are listed in [NOTICE](NOTICE).
