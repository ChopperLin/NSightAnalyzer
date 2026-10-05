# Atom-first roadmap

This is the implementation order. Probe 0.44 is the current legacy oracle.

## R0 — Correct the baseline (complete)

- replace public-only/capture/Perf SDK assumptions with the real Viewer-hosted source;
- limit product scope to existing .ngfx-gputrace;
- define exact version, provenance, availability, identity, paging, and failure rules;
- keep generic probing controls out of product contracts.

## R1 — Viewer adapter and first atom spine (complete)

- direct NsightViewer2026_2 build 37991608 adapter, with no generic provider interface;
- bounded one-shot Viewer lifecycle using request/trace identity, schema validation, timeout,
  CrashReporter detection, and local artifacts;
- trace identity/capability atom;
- event page atom and stable EventKey;
- event-parameters atom;
- range-metrics atom with long-form projection.

R1 closes against the generated trace, the real single-frame trace, and frame 0/frame 29 of the
30-frame trace.

## R2 — Shader and instruction atoms (complete)

- range shader inventory;
- focused shader profile;
- shader-specific instruction mix;
- range-level instruction mix;
- DXIL/HLSL/SASS-address source correlation;
- explicit missing-PDB, Pro-only SASS, and unsupported draw-scope states.

The c709 shader values are the fixed real-trace oracle.

## R3 — Analysis and counter atoms (complete)

- per-frame/range Trace Analysis projection;
- raw counter catalog/rows with duplicate occurrence identity;
- bounded artifact lifecycle for the built-in export.

The counter catalog/value path is closed on the single-frame report. Trace Analysis is closed by
the product CLI on the 30-frame report with an explicit GBuffer seed and all 30 frame rows.
The P0 denominator is now frozen; wrapper work may begin only after the verification/commit gate.

## R4 — Session transport (complete)

Replace repeated one-shot report loading with one serialized Viewer session per trace. Session reuse
must not change atom contracts, output ordering, availability, or provenance. A fresh-session replay
of the same atom must return the same semantic result.

The product now launches one normal foreground Viewer per exact trace snapshot, serializes requests,
and closes it on idle timeout or `viewer-session.close`. The Viewer-owned CrashReporter pipe guardian
is recorded as the session baseline; only Viewer loss or an additional/replacement guardian
invalidates the session. Real-trace dogfood verified PID reuse, explicit close, and rebuild after a
forced Viewer exit.

The first bounded semantic-response cache is also closed. Stable, complete range-shader snapshots
are retained only inside that exact Viewer process. Cache hits still re-resolve, establish, and
verify the requested EventKey; GBuffer and DeferredLighting use distinct keys. On the single-frame
fixture, repeated range-shader retrieval fell from 9.18-10.17 s (9.44 s average) to 1.21-1.34 s,
while alternating GBuffer/Deferred/GBuffer preserved each page's result hash. Instruction Mix and
all error, timeout, truncated, action, and artifact-writing paths remain uncached.

## R5 — Deterministic wrappers (in progress)

The first comparative-hotspot slice is implemented:

1. `resolve-event`: exact name + explicit occurrence, optionally bounded by an exact ancestor;
2. `inspect-pass`: complete metric closure plus coverage-labelled shader Top-N; v1 leaves range
   Instruction Mix to its explicit atom and reports that family as not requested;
3. `compare-ranges`: same-trace or cross-trace target-minus-baseline deltas with paged metrics.
4. `compare-frame-timing`: same-trace alignment of two explicit same-grain events, an explicit
   Trace Analysis seed, and an explicit Present queue; it returns only display-based interval
   decomposition, validation, and target-minus-baseline deltas.

Still deferred until real dogfood proves their shape:

5. general `compare-frames` pass-matching policy;
6. `inspect-shader` wrapper (the source atom remains callable directly);
7. `export-agent-dataset`.

Wrappers resolve, page, join, filter, and compare. They do not diagnose.

## R5.5 — Agent-facing surface and cost (complete)

Dogfood of the CLI as an agent tool exposed four blockers, all now closed:

- range PC-sampling atoms accepted a single-command scope and returned a plausible result;
  they now refuse it (SCP-002);
- discovery required knowing the word `capabilities`, and the catalog carried only prose;
  `--help` and a bare invocation now return it with machine-readable parameters;
- `resolve-event` matched exactly, so the natural first query missed; `--event-name-mode contains`
  matches substrings and refuses an ambiguous query with the candidates' ordinals;
- orientation meant paging the whole event tree; `trace.outline` returns the range skeleton
  in one call.

Cost on the single-frame report: event page 3.5 s -> 2.15 s, `trace.outline` 2 s for the whole
skeleton, `inspect-pass` 6 calls / 77 s -> two atom calls / 16.91 s including a fresh Viewer and
first snapshot, then 3.02 s when repeated, `compare-ranges` 12 calls -> two strict bundle calls plus
its exact-event reads, and repeated range-shader calls -> about 1.3 s after the first stable snapshot.
Two pre-existing ordering instabilities were found and fixed while diffing before against after.

## R5.6 — Compact Agent interface gaps (complete)

The [Agent interface comparison](agent-interface-matrix.md) separates useful GPU Trace interaction
patterns from RenderDoc operations that require Graphics Capture replay. Probe 0.51 closed the three
gaps backed by already-proven facts:

1. `find-ranges` wrapper: bounded candidate search and Viewer-duration ordering over exact outline
   facts; return candidates instead of requiring a known occurrence;
2. `trace.range-metric-catalog/v1` atom: compact table/column/unit discovery for one exact range,
   with no metric value payload;
3. `trace.shader-profile/v1` atom: one exact stage/hash/occurrence shader fact, reusing the verified
   shader snapshot and cache;
4. `find-events` wrapper: bounded substring search across ordinary API events and ranges, with
   optional exact-ancestor scoping and no ambiguity failure.

The execution-context candidate was audited and rejected for this report/build: selecting an exact
event changed no state model among 380 decoded models, and draw/range selection exposed the same 506
Shader Pipelines roots. A Vulkan descriptor-heap trace with explicit bind and barrier commands also
created no binding/resource model among 381 models; marker and draw selection exposed the same four
pipeline roots. Binding and resource metadata are unavailable for the tested build/reports and may
be reopened only by a future real fixture that actually materializes those facts.

Real single-frame dogfood returned warm `find-ranges` in 2.10 s (one 4,951-event traversal), the
88-table header-only metric catalog in 1.88 s, and a cached singular shader profile in 1.25 s. Their
compact JSON results were 3.6-22.4 KB. The first shader snapshot remained the expensive operation at
9.23 s in an already-open Viewer; the profile projection adds no second model read.
The first `find-events ClearRenderTargetView` request took 8.60 s and returned 20 of 52 matches in
10.7 KB; the next exact-scope request took 2.08 s and returned all seven matches in 3.7 KB.

Resource contents, pixel history/debugging, post-VS mesh data and render-target export remain
outside GPU Trace and are not roadmap candidates. New operations must report dogfood call count,
bytes and elapsed time as well as semantic closure.

## R6 — Skill and dogfood (ongoing)

The packaged Agent Skill routes investigations through the verified atom/wrapper surface without
requiring product source reads or one-off analysis scripts. Dogfood records missing facts, call count, bytes, elapsed time,
stale-selection failures, and unsupported claims.

When dogfood exposes a gap, promote only the smallest repeatable objective fact into an atom. Search
policy and bottleneck judgment remain above the atom layer.

## R5.7 — Concise defaults and focused evidence (CLI v2)

- Default concise JSON, 20-item pages and Top 5 shaders; detailed fields opt in with `--detail`.
- Short `capabilities` plus `describe <operation>` / operation-level help and structured recovery.
- Paged `inspect-pass`; section-driven range reads; metric-only comparisons avoid shader and Instruction Mix activation.
- Shader source resolves stage/hash/occurrence against the verified inventory and checks final semantic identity.
- Packaged Skill routes by intent and includes evidence and recovery references; no causal judgment enters wrappers.

## R5.8 — Accurate focused retrieval and portable clients (CLI v2.1)

- Missing samples, absent tables, ambiguous shader joins, and numeric source/resolution changes remain explicit.
- `find-metrics` locates exact metric cells; `find-source-hotspots` ranks a complete explicit source view with coverage.
- `--sections timing` and `compare-timings` avoid metric/shader work for timing questions. Batch statistics use the
  current page and exact context, with explicit caller sample groups for repeated measurements.
- Bounded metric snapshots reuse values only after fresh catalog/scope validation in the exact live session.
- `doctor`, `version`, machine-readable option constraints, and absolute `--workspace` support shell clients
  independently of their working directory or Agent brand.
- The Skill requires configuration/control-variable evidence for causal claims, including SER combined with
  thread-reordering changes. Timing/counter differences alone cannot identify which configuration changed.

Further product atoms still require a missing objective fact and a real fixture. Automatic matching across
unrelated traces, causal scoring, and statistical significance are not inferred from these summaries.

Earlier sections record v1 behavior and measurements; current behavior is documented in README and architecture.
