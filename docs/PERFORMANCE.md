# Performance

This page explains what the engine does to keep per-request cost low, and gives the numbers we measured, the
machine, and the exact commands to reproduce them. **We have not benchmarked any named third-party rule engine.**
The comparisons here are against two in-repo baselines described below, and they are not uniformly flattering: the
engine wins clearly on deep payloads and large bodies, and loses on small single-event requests (see "Summary").

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

## What is compared

- **Engine**: the real serving path, working directly on the request's UTF-8 JSON (`JsonDocument` + compiled
  conditions + merge plan written with `Utf8JsonWriter`). No model conversion in or out.
- **Wrapped** (`tst/RuleEngine.Wrapped`): *a typical wrapped-engine architecture, hand-written and given every
  advantage (source-generated System.Text.Json, direct typed-property access)*. It is NOT a named third-party
  product and we make no claim about any real product. It does: bytes -> deserialize into a deep DTO model ->
  evaluate the same rules by reading properties of the typed object -> build a result model -> serialize -> bytes.
  Rules: `startsWith(eventType, prefix) AND country ==ci "USA" AND raceNumber > min` with fragment
  `{rule{i}:"yes", track:{trackName}, meta:{id:{eventId}}}`, half of the rules matching the test event. The rules
  are hard-coded loops over a small array, so it carries no rule-language interpretation overhead at all: it is
  the best case for this architecture, not a general engine.
- **Naive** (`Naive_` rows): JsonNode tree, hand-interpreted conditions, `DeepClone` merge. Lighter than a real
  DTO-based wrapper; kept for continuity with the first version of this page.

**Deep model** (`EventDto`): about 45 properties over 4 nesting levels (event -> venue -> track, event ->
competitors[8] -> form[4] / trainer, event -> markets[3] -> selections[8]), `Dictionary<string, object?>` and
`Dictionary<string, string>` bags at several levels, enums, `DateTime`, `DateTimeOffset`, nullable ints, doubles and
decimals. Result model: `EvalResult` (typed track/meta plus the `rule{i}` flags via `[JsonExtensionData]`).
**Deep payload**: 9,033 bytes per event (one event); 904,333 bytes for the 100-event batch. **Small payload**: the
flat 12-field event, 303 bytes per event; 30,343 bytes for 100. Both engines read the identical bytes
(`Payloads.cs` generates them; `emit` writes them to files for the HTTP runs). The engine ignores everything it does
not read; the wrapped baseline must bind all of it. One-event runs use an event that matches half of the rules;
batch runs use 100 events of which about a third are non-racing and many have low race numbers.

## Benchmarks (in-process)

Project: `tst/RuleEngine.Benchmarks` (BenchmarkDotNet 0.15.4, `MemoryDiagnoser`). It is not a test project, so
`dotnet test RuleEngine.slnx` does not run it. It calls the engine's real serving path (`ApplyV2TemplatesToPlan`
plus `MergePlan.WriteTo`) without HTTP or Redis. Every "Evaluate"/"Wrapped" row starts from event bytes and ends
with response bytes.

Machine: 13th Gen Intel Core i7-13700KF (6 P + 8 E cores, 12 logical shown to WSL2), Linux 6.18 WSL2 on Windows,
Ubuntu 22.04, .NET SDK 10.0.103 / runtime 10.0.3, X64 RyuJIT x86-64-v3, Release, `InvariantGlobalization=true`
(as the service). Job: BenchmarkDotNet `--job short` (3 warmup, 3 measured iterations, 1 launch), on a shared
desktop with other containers idle-running, so treat differences under ~15% as noise. The short job has wide error
bars; a full default run would be tighter and we did not do one.

```bash
dotnet run -c Release --project tst/RuleEngine.Benchmarks -- --filter '*Evaluate_1Event*' '*Evaluate_100Events*' '*Evaluate_Deep*' '*Wrapped*' '*Naive*' --job short
# everything (adds Compile_* and Match_*):
dotnet run -c Release --project tst/RuleEngine.Benchmarks -- --filter '*' --job short
```

Mean time per operation (allocated memory per operation in brackets). Lower is better.

| Payload / batch | Rules | Engine | Wrapped baseline | Wrapped / Engine |
|---|---|---|---|---|
| Deep, 1 event | 1 | 15.0 us (4.8 KB) | 32.2 us (39 KB) | 2.2x slower |
| Deep, 1 event | 10 | 17.4 us (8.4 KB) | 31.5 us (40 KB) | 1.8x slower |
| Deep, 1 event | 100 | 44.7 us (49 KB) | 33.6 us (45 KB) | **0.75x (engine slower)** |
| Deep, 100 events | 1 | 1.60 ms (426 KB) | 3.34 ms (3.7 MB) | 2.1x slower |
| Deep, 100 events | 10 | 1.72 ms (679 KB) | 3.46 ms (3.7 MB) | 2.0x slower |
| Deep, 100 events | 100 | 3.71 ms (3.3 MB) | 3.47 ms (4.0 MB) | **0.94x (level / engine slightly slower)** |
| Small, 1 event | 1 | 1.42 us (3.1 KB) | 0.81 us (1.3 KB) | **0.57x (engine slower)** |
| Small, 1 event | 10 | 3.67 us (6.6 KB) | 0.99 us (1.6 KB) | **0.27x (engine slower)** |
| Small, 1 event | 100 | 27.7 us (47 KB) | 3.2 us (6.7 KB) | **0.12x (engine slower)** |
| Small, 100 events | 1 | 106 us (247 KB) | 66 us (111 KB) | **0.62x (engine slower)** |
| Small, 100 events | 10 | 295 us (500 KB) | 82 us (132 KB) | **0.28x (engine slower)** |
| Small, 100 events | 100 | 2.15 ms (3.2 MB) | 226 us (449 KB) | **0.11x (engine slower)** |

Naive JsonNode baseline (small payload): 1 event 1.65 / 3.64 / 23.3 us; 100 events 125 us / 272 us / 1.88 ms for
1 / 10 / 100 rules (allocations 4.4 / 9.2 / 65 KB and 375 KB / 708 KB / 4.3 MB).

Compile (once per template version, not per request; measured earlier, workload unchanged): flat template 0.65 /
6.3 / 64 us for 1 / 10 / 100 rules; JsonLogic 0.88 / 8.8 / 92 us. Match-only rows from the earlier run used an event
that matched no rule (0.36 / 1.06 / 8.4 us) and are not comparable with the rows above.

### Reading the in-process numbers

- **Model binding is the cost the design avoids.** On the 9 KB deep payload, the engine's parse-and-read costs
  about 15 us and 4.8 KB; the wrapped baseline spends about 31 us and 39 KB binding the same bytes (2x time, 8x
  allocation) whatever the rule count. For 100 events that is 1.6 ms vs 3.3 ms and 0.4 MB vs 3.7 MB, with the wrapped
  side already promoting garbage to Gen1 in the allocation columns.
- **But the engine's per-rule cost is higher than a hand-coded loop.** The wrapped baseline's rules are a hard-coded
  three-condition loop over typed properties and cost about 22 ns per rule (small, 1 event). The engine pays
  roughly 270 ns per rule when about half of the rules match (small, 1 event: 27.7 us / 100 rules), mostly in string
  conditions (`GetString()` allocation, culture-invariant compare) and building the merge plan for each matched rule.
  With 100 rules that per-rule cost overtakes the saved binding cost on a single deep event and roughly cancels it on
  a deep batch; on small payloads there is no binding cost to save, so the wrapped baseline wins by 2x-9x.
- **Against the lighter JsonNode baseline** the earlier result stands: at 100 rules the engine is slower (27.7 vs
  23.3 us; 2.15 vs 1.88 ms for the batch) and it allocates less at every size. The earlier version of this page
  reported 9.1 us for 100 rules and one event; that event matched no rule, so it understated the merge cost. The
  numbers above use an event matching half the rules.
- The lookup/merge micro-cost is real, but it is small next to HTTP, JSON body handling and (for wrapped engines)
  model binding: over HTTP the single-event gap shrinks to 10-20% (below). It is the obvious next optimisation
  target: pre-computing string operands and avoiding `GetString()` per condition, and a cheaper plan for matched
  rules.
- JsonLogic and flat rules are indistinguishable in speed, as designed (earlier run).

## End-to-end HTTP (container, both hosts)

Both hosts are NativeAOT binaries on the same `runtime-deps:10.0-noble-chiseled` base, run as containers on one
Docker network, one at a time under load, driven by [`hey`](https://github.com/rakyll/hey) (`williamyeh/hey` image,
host networking, `-c 16`, 15 s per row after a 3 s warm-up of each).

- **Engine**: the real service image (`docker build -t rule-engine:local .`, 38 MB), Redis alongside, template
  loaded with PUT, request `POST /v2/templates/{id}/evaluate`.
- **Wrapped host** (`tst/RuleEngine.WrappedHost`, not shipped; 24.5 MB image): ASP.NET Core minimal API, model
  binding into the deep DTO with source-generated System.Text.Json, result returned as a typed model, rules in memory
  (`RULES` env var). It has **no Redis, no OpenTelemetry, no health-check registration and no template loading**,
  so it does strictly less work per request than the real service; the comparison is generous to it.

```bash
D=$(mktemp -d)
dotnet run -c Release --project tst/RuleEngine.Benchmarks -- emit $D          # payloads + template bodies, prints sizes
docker build -t rule-engine:local .
docker build -f tst/RuleEngine.WrappedHost/Dockerfile -t rule-engine-wrapped:local .
docker network create re-net
docker run -d --name re-redis --network re-net redis:7-alpine
docker run -d --name re-svc --network re-net -p 18080:8080 -e ConnectionStrings__Redis=re-redis:6379 -e ENVIRONMENT=bench rule-engine:local
docker run -d --name re-w10  --network re-net -p 18081:8080 -e RULES=10  rule-engine-wrapped:local
docker run -d --name re-w100 --network re-net -p 18082:8080 -e RULES=100 rule-engine-wrapped:local
for n in 10 100; do curl -X PUT localhost:18080/v2/templates/t$n -H 'content-type: application/json' -d @$D/t$n.json; done
# engine:   POST localhost:18080/v2/templates/t10/evaluate      with deep1.json / deep100.json / small1.json / small100.json
# wrapped:  POST localhost:18081/evaluate | /evaluate-batch | /evaluate-small | /evaluate-small-batch (18082 for 100 rules)
docker run --rm --network host -v $D:/d williamyeh/hey -z 15s -c 16 -m POST -T application/json -D /d/deep1.json http://localhost:18080/v2/templates/t10/evaluate
```

Requests per second (higher is better), p50 / p99 latency, all 200 OK:

| Scenario (16 connections) | Engine | Wrapped host | Engine vs wrapped |
|---|---|---|---|
| Deep, 1 event (9 KB), 10 rules | 18,900 rps, 0.7 / 3.0 ms | 21,100 rps, 0.6 / 2.2 ms | wrapped 12% faster |
| Deep, 1 event, 100 rules | 17,700 rps, 0.7 / 2.9 ms | 20,800 rps, 0.7 / 2.2 ms | wrapped 17% faster |
| Deep, 100 events (904 KB), 10 rules | **966 rps**, 15.9 / 34.5 ms | 148 rps, 105 / 201 ms | **engine 6.5x faster** |
| Deep, 100 events, 100 rules | **418 rps**, 36.4 / 77.7 ms | 137 rps, 111 / 230 ms | **engine 3.1x faster** |
| Small, 1 event (303 B), 10 rules | 28,600 rps, 0.5 / 1.8 ms | 32,800 rps, 0.4 / 1.5 ms | wrapped 15% faster |
| Small, 1 event, 100 rules | 20,700 rps, 0.6 / 2.7 ms | 31,400 rps, 0.4 / 1.5 ms | wrapped 52% faster |
| Small, 100 events (30 KB), 10 rules | 6,260 rps, 2.6 / 6.8 ms | 15,150 rps, 0.9 / 3.1 ms | wrapped 2.4x faster |
| Small, 100 events, 100 rules | 594 rps, 26.1 / 49.0 ms | 7,940 rps, 1.9 / 4.7 ms | wrapped 13x faster |
| Deep, 1 event, 10 rules, 1 connection | 3,030 rps, 0.3 / 0.5 ms | 3,100 rps, 0.3 / 0.5 ms | equal |

### Reading the HTTP numbers

- Single small or deep events over HTTP: the wrapped host is 10-50% faster. HTTP, Kestrel and JSON body handling
  dominate a 30-50 us request, so the engine's micro-costs matter a little (up to 50% at 100 rules) and the saved
  model binding does not show at 9 KB. At one connection they are equal.
- Large bodies (100 deep events, 0.9 MB): the engine is 3-6.5x faster. The wrapped host binds ~900 KB into ~3.7 MB
  of objects per request with 16 in flight, which is where model-conversion cost and GC pressure show.
- Small batches with many rules: the engine is far slower (13x at 100 rules x 100 events). The merge-plan and
  string-condition cost per matched rule, multiplied by 100 events, is the whole request. This is the clearest
  weak spot the benchmarks found.
- So the claim the data supports is narrow: **working on the JSON directly wins when payloads are large or deep and
  the rule count is modest; it does not win for small payloads, and the current per-rule cost erodes the win as
  rules and matches grow.**

Caveats: WSL2 on a shared desktop; load generator, Redis and both services compete for the same 12 logical CPUs,
no CPU pinning or limits; single 15 s run per row, no repeats, so 10-15% differences are within noise; other
containers were idle-running; `hey` is closed-loop (no coordinated-omission correction); the wrapped host is a
hand-written best case (hard-coded rules, no Redis/telemetry) so real wrapped services would carry additional
costs we did not measure; the wrapped host emits `"track":null,"meta":null` for non-matching events where the engine
emits `{}` (a few extra bytes); the engine's rule set is loaded from data and evaluated generically, which the
wrapped baseline does not do (a feature, not a cost, that this benchmark does not credit). Memory at rest was not
re-measured this time (earlier: about 28 MiB after loading, 59 MiB after load runs; image 38 MB vs 24.5 MB).
Numbers from a different machine will differ; use the commands above.
