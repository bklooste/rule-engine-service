# Performance

This page explains what the engine does to keep per-request cost low, and gives the numbers we measured, the
machine, and the exact commands to reproduce them. **We have not benchmarked any other rule engine.** The only
comparison here is against a deliberately naive in-repo baseline, described below, and that comparison is not
uniformly flattering (see "Surprises").

## Design choices that affect speed

Each of these is visible in the code:

- **Rules are compiled once per template version.** `CompiledV2Template.Compile` turns every `dataToMatch` into
  `CompiledCondition` objects (`Compiled/CompiledCondition.cs`): the operator, wildcard kind, numeric operand and
  `ago()` offset are parsed at compile time, and evaluation is one virtual call. JsonLogic input
  (`Compiled/JsonLogicConditionCompiler.cs`) compiles to the same condition objects, so a JsonLogic rule costs the
  same to evaluate as a flat one (the benchmarks below show identical timings). `Expression.Compile` is never used,
  because it only runs interpreted under NativeAOT.
- **Events are read straight off `JsonElement`.** The request body is parsed into a `JsonDocument` (UTF-8 buffer)
  and conditions read properties from it. There is no POCO or dictionary-of-objects deserialisation of the event.
  One `EventPropertyIndex` (a case-insensitive `Dictionary<string, JsonElement>`, built lazily) is shared by all
  rules for that event, instead of scanning the event per rule.
- **Fragments are immutable and shared.** Each rule's `matchFragment` is parsed once and pre-scanned for `{path}`
  tokens (`Compiled/CompiledFragment.cs`). Evaluations reference those shared nodes rather than cloning them.
- **The merge produces a plan, not a tree.** `MergePlan` records ordered keys plus references to fragment nodes and
  small overlays, and is written directly to the response with `Utf8JsonWriter` (`Api/MergePlanResult.cs`). No
  `JsonObject` result tree is built on the evaluate path.
- **Version-checked cache.** Compiled templates live in a per-process `MemoryCache` keyed by id and Redis version;
  the version key itself is cached in `HybridCache` for a short window, so a warm evaluate does no Redis I/O and no
  recompilation (`DynamicRulesEngine.LoadV2Async`).
- **Native AOT image.** The published container is a NativeAOT binary on a chiselled runtime-deps base (see
  `Dockerfile`). What we measured: 38 MB image and about 21-28 MiB resident when idle. We did not measure start-up
  time precisely. Note the project's own history found that AOT is a memory/start-up win, not a throughput win;
  the benchmarks below run under JIT.

Things that are not fast, for balance: string conditions call `JsonElement.GetString()` (an allocation per
condition) and use `InvariantCultureIgnoreCase` comparisons; the property index is a dictionary allocated per event.

## Benchmarks (in-process)

Project: `tst/RuleEngine.Benchmarks` (BenchmarkDotNet 0.15.4, `MemoryDiagnoser`). It is not a test project, so
`dotnet test RuleEngine.slnx` does not run it. It calls the engine's real serving path (`ApplyV2TemplatesToPlan`
plus `MergePlan.WriteTo`) without HTTP or Redis.

Workload: N rules (1 / 10 / 100), each with three conditions (a prefix wildcard, a case-insensitive equality, a
numeric `>`) and a fragment with two `{path}` tokens and a nested object; about half of the rules match the sample
event (a 12-field racing event). "Evaluate" starts from UTF-8 event bytes and ends with UTF-8 response bytes.
"Flat" is the string DSL; "JsonLogic" is the same rules as JsonLogic.

**Naive baseline** (rows prefixed `Naive_`): parse the event into a `JsonNode` tree, read the three fields it
needs once, interpret the same three conditions by hand with `OrdinalIgnoreCase`, `DeepClone` matched fragments
into a `JsonObject`, and `ToJsonString`. It is not another rule engine. It is also hand-specialised to these three
fields (so it is faster than a generic naive interpreter would be).

Machine: 13th Gen Intel Core i7-13700KF (6 P + 8 E cores, 12 logical shown to WSL2), Linux 6.18 WSL2 on Windows,
Ubuntu 22.04, .NET SDK 10.0.103 / runtime 10.0.3, X64 RyuJIT x86-64-v3, Release, `InvariantGlobalization=true`
(as the service). Job: BenchmarkDotNet `--job short` (3 warmup, 3 measured iterations, 1 launch), on a shared
desktop, so treat differences under ~15% as noise.

Command:

```bash
dotnet run -c Release --project tst/RuleEngine.Benchmarks -- --filter '*' --job short
```

(Omit `--job short` for the full default job; it takes much longer.)

Means (allocations are per operation):

| Benchmark | 1 rule | 10 rules | 100 rules |
|---|---|---|---|
| Compile flat template (once per version) | 0.65 us / 2.0 KB | 6.3 us / 19 KB | 64 us / 193 KB |
| Compile JsonLogic template | 0.88 us / 2.5 KB | 8.8 us / 24 KB | 92 us / 240 KB |
| Match only, flat (pre-parsed event) | 0.36 us / 1.7 KB | 1.06 us / 2.3 KB | 8.4 us / 7.9 KB |
| Match only, JsonLogic | 0.37 us / 1.7 KB | 1.07 us / 2.3 KB | 8.5 us / 7.9 KB |
| Evaluate, 1 event (parse + match + merge + write) | 0.76 us / 2.1 KB | 1.48 us / 2.7 KB | 9.1 us / 8.3 KB |
| Evaluate, JsonLogic, 1 event | 0.75 us / 2.1 KB | 1.54 us / 2.7 KB | 9.2 us / 8.3 KB |
| Evaluate, 100-event batch | 102 us / 247 KB | 284 us / 500 KB | 2.09 ms / 3.2 MB |
| Naive baseline, 1 event | 1.00 us / 3.1 KB | 1.46 us / 3.7 KB | 5.8 us / 9.7 KB |
| Naive baseline, 100-event batch | 121 us / 375 KB | 269 us / 708 KB | 1.83 ms / 4.3 MB |

Rough per-rule cost of matching is about 85 ns for three conditions.

### Surprises

- Against the naive baseline the engine is ahead at 1 rule (about 25%) and on allocations everywhere (about 35-45%
  less), level at 10 rules, and **slower at 100 rules** (9.1 us vs 5.8 us for one event, 2.09 ms vs 1.83 ms for a
  100-event batch). The baseline gets an advantage the engine cannot: it knows which three fields matter and reads
  them once per event, where the engine does a generic property lookup, `GetString()` and culture-invariant
  case-insensitive compare per condition. So the "compiled" design has not been shown to beat a hand-written
  specialisation on CPU time; what it demonstrably gives is generic rules from data, no per-request parsing of the
  rule, fewer allocations and no result tree. The per-condition costs (string allocation, culture compare) look like
  the next optimisation target.
- JsonLogic and flat rules are indistinguishable in speed, as designed.

## End-to-end HTTP (container)

The service built with `docker build -t rule-engine:local .` (NativeAOT image), run next to a `redis:7-alpine`
container on a user-defined Docker network, driven by [`hey`](https://github.com/rakyll/hey) (`williamyeh/hey`
image, host networking) for 15 s per row after a warm-up. Same rule shape as above; the request is
`POST /v2/templates/{id}/evaluate`. Templates were loaded with PUT first, so the run measures the warm path.

```bash
docker network create re-net
docker run -d --name re-redis --network re-net redis:7-alpine
docker run -d --name re-svc --network re-net -p 18080:8080 \
  -e ConnectionStrings__Redis=re-redis:6379 -e ENVIRONMENT=bench rule-engine:local
curl -X PUT localhost:18080/v2/templates/t10 -H 'content-type: application/json' -d @t10.json
docker run --rm --network host -v "$PWD":/d williamyeh/hey -z 15s -c 16 -m POST \
  -T application/json -D /d/e1.json http://localhost:18080/v2/templates/t10/evaluate
```

| Scenario | Concurrency | Requests/sec | p50 | p99 |
|---|---|---|---|---|
| 10 rules, 1 event | 16 | 30,400 | 0.5 ms | 1.7 ms |
| 10 rules, 1 event | 1 | 3,880 | 0.3 ms | 0.3 ms |
| 100 rules, 1 event | 16 | 25,500 | 0.5 ms | 2.1 ms |
| 10 rules, 100 events | 16 | 5,480 | 2.9 ms | 7.9 ms |
| 100 rules, 100 events | 16 | 600 | 25.6 ms | 49 ms |

Service memory (docker stats) was about 28 MiB after loading, 59 MiB after the load runs; image size 38 MB.

Caveats: WSL2 on a shared desktop; the load generator, Redis and the service all compete for the same 12 logical
CPUs with no pinning; the container had no CPU limit; single run per row, no repeat variance measured; `hey` is
closed-loop, so latency percentiles do not include coordinated-omission correction; the 100-event/100-rule row
returns a large merged response (dominated by matching and writing 100 x ~50 rules), so it is not a small-request
figure. Numbers from a different machine will differ; use the commands above.
