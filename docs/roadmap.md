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

## R4 — Session transport

Replace repeated one-shot report loading with one serialized Viewer session per trace. Session reuse
must not change atom contracts, output ordering, availability, or provenance. A fresh-session replay
of the same atom must return the same semantic result.

## R5 — Deterministic wrappers

In dependency order:

1. resolve-event;
2. inspect-pass;
3. inspect-shader;
4. compare-ranges;
5. compare-frames;
6. export-agent-dataset.

Wrappers resolve, page, join, filter, and compare. They do not diagnose.

## R6 — Skill and dogfood

Add the Agent Skill only after the atom/wrapper surface can complete real investigations without
source reads or one-off scripts. Dogfood records missing facts, call count, bytes, elapsed time,
stale-selection failures, and unsupported claims.

When dogfood exposes a gap, promote only the smallest repeatable objective fact into an atom. Search
policy and bottleneck judgment remain above the atom layer.
