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
    NsightViewer2026_2 adapter
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
`inspect-pass` closes all available source pages before emitting complete metrics and a
coverage-labelled shader Top-N. V1 does not request the independently stateful range Instruction
Mix model; the nested section is `unavailable` with
`trace.range_instruction_mix_not_requested` and no `items`/`totalCount`, while the explicit atom
remains available for callers that need it. `compare-ranges` joins semantic
identities, retains each side's source ordinal, and pages metric deltas; target-only and
baseline-only are never coerced to zero.

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

Trace identity is the absolute local file snapshot (length,lastWriteTimeUtcTicks) by default, with
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

The built-in counter export is a local-artifact atom. It retains validated TSV-like `.xls`
evidence, normalizes duplicate headers with occurrence/index identity, and removes only the exact
Viewer-created trace copy under that run directory.

## Atomic invocation

Every operation returns:

    operation + schemaVersion
      -> success: value + provenance + warnings
      -> failure: category + stable code + message + bounded detail

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

The first adapter is concrete and pinned:

    NsightViewer2026_2 / build 37991608 / bridge probe-0.51

A mismatched build returns adapterMismatch. A new Viewer build is re-probed and either gets a new
adapter mapping or an explicitly verified compatibility entry. No nominal 2026.x fallback exists.

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

## Repository layout

    src/NsightAnalyzer/
      Cli/
      Contracts/
      Operations/
      Adapters/NsightViewer2026_2/
    tools/probes/qt-model-bridge/    frozen probing/legacy oracle
    tools/verification/              contract and optional Viewer closure
    docs/
    .local/                          reports, raw output, builds and runs

The product bridge is currently the constrained semantic subset of the committed SolidProbe
plugin; generic discovery controls remain probe-only. Do not split projects or add interfaces until
a second implementation proves the seam.
