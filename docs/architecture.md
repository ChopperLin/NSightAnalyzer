# NSightAnalyzer architecture

## Source reality

No discovered documented Nsight Graphics or Nsight Perf SDK reads all performance data from an
existing .ngfx-gputrace. The matching Nsight Viewer already decodes that report and materializes
the needed facts in Qt item models. NSightAnalyzer therefore uses a QGenericPlugin hosted by that
exact Viewer build.

This is an unsupported implementation-detail adapter. Its trustworthiness comes from strict build
pinning, exact provenance, bounded traversal, selection/stability checks, real-trace closure, and
explicit capability failures—not from pretending it is an official SDK.

## Semantic DAG

    Agent Skill
        |
        | hypotheses, causality, confidence, recommendations
        v
    deterministic wrappers
        |
        | identity resolution, paging, filtering, joins, comparison
        v
    semantic atomic operations
        |
        | typed request/result, exact scope, provenance
        v
    exact version-pinned compatibility map
        |
        | version-specific model discovery, selection and projection
        v
    Viewer lifecycle transport -> SolidProbe -> decoded Qt models

Transport is orthogonal to semantics. Reusing a Viewer changes invocation cost, not returned facts.

## Atom admission

An operation is an atom only when:

1. the decoded report model or a deterministic projection is the source of truth;
2. input is a trace identity plus an exact EventKey/ShaderKey or a bounded page;
3. output has task-independent meaning and no diagnostic priority or cause;
4. collections are complete or explicitly paged/truncated;
5. the result closes against a probe or fixture oracle;
6. every unavailable relation is preserved rather than guessed.

Atomicity is semantic. A range-metrics atom may select an event, wait for 88 tables, validate the
selection, and flatten hundreds of cells while still returning one exact range fact.

Imperative operations such as open-report, select-event, click-panel, load-source, and dump-model are
not atoms. They remain adapter/transport implementation details or probe-only controls.

## Wrapper admission

Wrappers are added only after the P0 atom denominator closes. They may:

- resolve a marker name/path/occurrence to an exact EventKey;
- concatenate pages;
- select exact metrics requested by the caller;
- join event, metric, shader, and source facts on returned identities;
- compare two exact ranges or frames;
- emit bounded deterministic summaries.

The first wrapper slice is exposed by the same JSON CLI with descriptor `layer: wrapper`.
`inspect-pass` closes requested source families before returning a bounded metric page and
coverage-labelled shader Top-N. `compare-ranges` defaults to metrics; callers explicitly select
`timing`, `metrics`, `shaders`, and/or `instruction-mix` with `--sections`. Timing-only comparisons
read two exact event rows and activate no metric or shader models. Unrequested families are omitted
and declared by the `sections` list, not represented as empty or unavailable facts. Every requested
family must succeed. Instruction Mix uses its independent uncached atom; it cannot fail an
unrelated metric-only request. Comparisons preserve each side's source ordinal, and target-only
and baseline-only values never become numeric zero.

`find-metrics` filters complete metric facts by name or exact source ordinal. `find-source-hotspots`
reads one complete source model, selects an explicit view, orders sampled rows, and reports coverage
against that view's sample total. `compare-timings` executes only the requested page of explicit
event pairs. Its statistics keep exact parent/object/thread contexts separate unless a caller supplies
`sampleGroup`; that label is external metadata, not proof of equivalent workload or configuration.

Wrappers may not invent a metric meaning, choose an optimization priority, infer causality, or turn
a candidate into a confirmed fact.

## Components

| Component | Owns | Must not own |
|---|---|---|
| Contracts | schema, EventKey/ShaderKey, pages, availability, provenance, errors | Qt or process details |
| Operations | validation and one semantic projection per atom | call-order state or diagnostic policy |
| NsightViewer2026_2 | exact build mapping, model selection, stabilization, raw-to-semantic projection | generic future-provider abstraction |
| Viewer transport | launch, request identity, timeout, crash detection, artifacts, cleanup | new facts |
| SolidProbe product bridge | bounded known-model reads on the GUI thread | generic model dump or unsafe probing knobs |
| Probe oracle | discovery, generic catalog/export, experiments | stable product contracts |
| Wrappers | resolution, paging, joins, explicit sorting/comparison | source fields or LLM judgment |
| Skill | workflow, evidence weighing, cause, confidence, recommendation | fabricated identities |

## Identity

Trace identity includes a hash of the normalized absolute path and the local file snapshot
(length,lastWriteTimeUtcTicks) by default, with
optional SHA-256 when a workflow needs cross-machine identity.

EventKey is returned by event enumeration and contains the preorder ordinal, tree path, displayed
event range, and frame/queue context available in the event row. Scoped atoms accept exact keys;
human names and occurrences are wrapper concerns.

ShaderKey contains at least stage and exact shader hash. When a hash occurs more than once in the
selected range, the returned pipeline/row occurrence remains part of the range-local identity.

Metrics and exported counters use long form:

    tableName, tableOccurrence, rowName, rowOccurrence,
    columnName, columnOccurrence, sourceOrdinal,
    displayValue, preciseValue, valueType, unit, description, availability

Duplicate names are never dictionary keys.

Missing sample cells/vectors are unavailable, explicit empty vectors remain empty, and measured
zero remains available zero. Shader joins require a unique stage/hash/name/pipeline tuple on each
side; range-local hash occurrence is preserved as evidence but is not a cross-range join key.
Metric comparisons expose numeric source and display resolution, including changes in those states
when the numeric delta is zero. Display resolution is not measurement uncertainty.

The built-in counter export is a local-artifact atom. It retains validated TSV-like `.xls`
evidence, normalizes duplicate headers with occurrence/index identity, and removes only the exact
Viewer-created trace copy under that run directory.

## Atomic invocation

CLI v2 defaults to 20-item pages, Top 5 shaders and JSON without indentation. Brief projections
omit metric prose and shader instruction vectors; `--detail` restores them within the same page.
`capabilities` is a short directory; `describe <operation>` returns exact parameters.
Descriptors include mutually exclusive and dependent options. `doctor` reports static dependency
checks separately from runtime checks; `version` identifies package content. An absolute `--workspace`
sets process-local run and session roots so shell clients can share a session from different directories.
The 1 MiB stdout bound is retained. If an actual `trace.range-shaders` envelope would cross it,
the CLI returns the largest fitting prefix, preserves the requested limit and collection total,
and advances `nextCursor` with `runtime.response_page_reduced`; no semantic fact is rewritten.

Every operation returns:

    operation + schemaVersion
      -> success: value + provenance + warnings
      -> failure: category + stable code + message + bounded detail + recovery action

Stable failure categories include invalidInput, notFound, unsupported, unavailable,
adapterMismatch, trace, viewer, timeout, and internal.

The adapter verifies:

- existing non-empty .ngfx-gputrace;
- exact Viewer version/build and installed bridge;
- fresh output with matching request and trace identity;
- expected bridge schema and complete status;
- exact selected EventKey/ShaderKey;
- model readiness/stability and explicit paging;
- controlled Viewer lifecycle and CrashReporter state: no survivor for one-shot runs, or exactly the
  startup-recorded guardian for a live reusable session.

## Versioning

The direct adapter has two concrete compatibility entries:

| Adapter identity | Compatibility profile | Viewer build | Qt runtime | Bridge compile Qt |
|---|---|---:|---:|---:|
| NsightViewer2026_2 | NsightViewerGpuTraceSemanticV1 | 37991608 | 6.8.1 | 6.8.1 |
| NsightViewer2026_3 | NsightViewerGpuTraceSemanticV1 | 38722833 | 6.10.2 | 6.8.1 |

Both require bridge `probe-0.52`. The 2026.3.1 entry reuses the proven semantic paths but has its own
exact host/Qt validation. It adds two warp-occupancy metric tables and a non-shader `Unattributed
Samples` summary row, which the shader projection explicitly excludes. A mismatched build returns
adapterMismatch. A new Viewer build is re-probed and either gets a new adapter mapping or an
explicitly verified compatibility entry. No nominal 2026.x fallback exists.

The profile contains semantic activation/projection code; host rows are admission evidence. A patch
release that closes the existing fixture suite adds one exact row to the profile instead of copying
an adapter. `viewer-host-targets.json` is embedded in the CLI as the canonical immutable admission
table; the bridge verifies the exact candidates supplied by that table. Only an observed semantic
difference creates profile-specific code.

## Session model

Product atoms use one serialized Viewer session per exact trace snapshot so arbitrary atom calls do
not reload a large report each time. The Viewer is launched normally; completely hidden operation is
not a requirement. A filesystem mailbox serializes GUI-thread requests, while an idle timeout and the
internal `viewer-session.close` command bound process lifetime.

Every request remains self-contained: it sets its own selection, waits, verifies the final selection,
and returns its own envelope. The normal Viewer-owned CrashReporter pipe guardian is recorded once at
startup and is not a crash signal. Viewer loss or an additional/replacement guardian invalidates the
session; a later atom creates a fresh session.

The first admitted response cache is deliberately narrower than session reuse. A complete,
non-timeout, stable `trace.range-shaders/v1` bridge response may be retained only in the current
Viewer process, under a key containing report identity, decoder/bridge version, mode, schema, exact
EventKey selector, and every adapter setting. On a hit the adapter resolves the EventKey again,
establishes that Qt selection, and verifies the final selected index before writing a fresh response
with the new request identity. The cache is bounded to four entries / 64 MiB with LRU eviction and
dies with the Viewer. Unavailable/error responses, truncated models, Instruction Mix, generic model
requests, shader-source actions, and local-artifact operations are excluded.

Range metric snapshots use a separate adapter cache under the exact session directory, bounded to
four entries of at most 8 MiB each. Admission requires a complete stable response from that session's
PID and completed request. Keys bind the trace path/snapshot, selector, process start time, session ID,
and adapter assembly; stored JSON is hashed. Each hit issues a fresh header-only metric catalog request,
verifies the current selection and table/header/row-count signature, and closes that proof against the
same live instance and completed request. Concurrent session changes force a miss. Metric values are
immutable facts of the unchanged report; Instruction Mix and source action results remain uncached.

## Repository layout

    src/NsightAnalyzer/
      Cli/
      Contracts/
      Operations/
      Adapters/NsightViewer2026_2/
        viewer-host-targets.json      exact admitted hosts and shared profile
    tools/probes/qt-model-bridge/    frozen probing/legacy oracle
    tools/verification/              contract and optional Viewer closure
    docs/
    .local/                          reports, raw output, builds and runs

The product bridge is currently the constrained semantic subset of the committed SolidProbe
plugin; generic discovery controls remain probe-only. Do not split projects or add provider interfaces
until a distinct semantic implementation, rather than another compatible host entry, proves the seam.
