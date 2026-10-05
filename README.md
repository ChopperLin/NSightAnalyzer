# NSightAnalyzer

NSightAnalyzer is an Agent-facing fact retrieval CLI for existing NVIDIA Nsight Graphics
.ngfx-gputrace reports.

The proven data path is deliberately honest:

    Agent -> deterministic wrapper -> semantic atom
          -> NsightViewer2026_2 adapter -> SolidProbe plugin
          -> report models decoded by the matching Nsight Viewer

Nsight Graphics 2026.2 build 37991608 must open the report because no discovered documented NVIDIA
SDK exposes the same existing-report data surface. The adapter is therefore unsupported and
version-pinned; it is not presented as an official report SDK.

The frozen Probe 0.44 oracle and the current `probe-0.51` product bridge have recovered and
validated:

- complete event/pass/marker/action navigation;
- range-scoped register, L2, VRAM, occupancy, stall, latency, queue, draw, and pipeline metrics;
- per-frame refresh and repeated-marker disambiguation;
- event parameters;
- shader inventory, samples, warps, static register/instruction data, stalls, and instruction mix;
- DXIL line hotspots and SASS-address-to-DXIL correlation;
- Trace Analysis models and the Viewer's raw counter export.

The committed probing implementation under tools/probes/qt-model-bridge is the legacy oracle.
The atom layer exposes 14 operations: capability/identity, outline, events, event parameters,
range-metric catalog/values, range instruction mix, range shader inventory, exact shader profile,
shader source, Trace Analysis, and raw-counter catalog/value retrieval. The deterministic wrapper
slice adds `find-events`, `find-ranges`, `resolve-event`, `inspect-pass`, `compare-ranges`,
`compare-frame-timing`, `find-metrics`, `find-source-hotspots`, and `compare-timings`. Agent diagnosis remains intentionally
above those wrappers.

CLI v2 returns concise JSON by default. Pages default to 20 items, shader Top-N to five;
all counts, scope, units, provenance and truncation remain explicit. `--detail` adds metric
descriptions and shader instruction vectors without disabling paging. `--pretty` only indents
JSON; the old `--compact` remains accepted but is unnecessary.

Start a targeted investigation with `find-ranges` (marker grain by default) or `find-events`.
Reuse exact returned keys. Use `trace.outline` when the tree context is needed, rather than
paging it before every question. `inspect-pass` returns paged metrics and shader Top-N;
`compare-ranges` defaults to metrics only; `--sections timing` reads just the exact event timings.
`--sections metrics,shaders,instruction-mix`
explicitly requests additional families, all of which must succeed. Unrequested families are
omitted and the selected families are recorded in `sections`. Instruction Mix remains uncached.

Comparison metric pages default to changed/unmatched facts, with `deltaFilter` and their own
page total. Overall matched/comparable counts still cover all selected metrics. Use
`--include-unchanged` only when unchanged deltas are needed; `--detail` controls descriptive fields.

`capabilities`, `--help`, or no arguments returns a short operation directory.
`describe inspect-pass` and `inspect-pass --help` return that operation's complete parameter
contract. `capabilities --detail` returns the full catalog when needed. Errors include a stable
code and a bounded structured recovery action. Occurrence arguments are zero-based.

Use `find-metrics --event-ordinal N --name-contains register` to find metric identities, then
`--source-ordinal N` to retrieve one exact cell. `find-source-hotspots --view dxil` ranks sampled
rows from one complete source view and reports sample coverage. `compare-timings --pairs-file
pairs.json` compares caller-specified EventKey pairs, with statistics over the current page only.
An optional caller `sampleGroup` explicitly groups repeated measurements; equal marker names alone
never establish equivalent context or causality.

`doctor` checks local dependencies without starting Viewer; `version` returns the CLI build and
package content fingerprint. Pass the same absolute `--workspace` to every client to share run/session
roots across working directories. PowerShell, cmd and Python subprocess invocations use the same
JSON protocol; the Skill has no Codex-only tool dependency.

Shader source requests now require stage, hash and occurrence, matching the returned ShaderKey.
The adapter resolves the exact row from a verified inventory and verifies its stage, hash,
name and pipeline again after source selection; a sorted proxy row is not an identity.
Source duplicates that this bridge cannot distinguish are explicitly unavailable. Cross-range shader
comparison uses unique stage/hash/name/pipeline tuples and rejects ambiguous matches. Missing sample
data never becomes zero, missing requested metric tables fail, and metric comparisons retain numeric
source and Viewer display resolution. Public envelopes use schema 2.1;
the pinned Viewer and `probe-0.51` bridge are unchanged.

Product atoms reuse one normally launched Viewer process per exact trace snapshot. The normal
CrashReporter pipe guardian is recorded as session baseline rather than treated as a crash; Viewer
exit or any additional/replacement guardian invalidates the session, and the next call rebuilds it.
Within that exact live session, a fully stabilized `trace.range-shaders` snapshot is retained in a
bounded memory cache. A hit still resolves and establishes the requested EventKey before returning
the recorded model; failures, timeouts, incomplete exports, Instruction Mix, and artifact-writing
operations are never cached.

Metric paging and search also reuse bounded, verified snapshots within the exact live Viewer session.
Every hit makes a fresh header-only catalog request to establish scope and verify table structure;
trace identity, process instance, completed request, and snapshot integrity must still match.

```powershell
dotnet build .\NSightAnalyzer.slnx -c Release
dotnet .\src\NsightAnalyzer\bin\Release\net9.0-windows\nsight-analyzer.dll `
  capabilities
```

Build the Agent Skill package and publish it to the sibling `lx6-hub\skills\nsight-analyzer`:

```powershell
pwsh -NoProfile -File .\scripts\build-package.ps1
```

The package contains the framework-dependent CLI host, `SKILL.md`, its analysis/recovery references, and a hash manifest. Use
`-SkipHubPublish` for an independent package build or `-HubPath <path>` for a non-sibling hub.
The package does not contain NVIDIA Viewer files or install the version-pinned SolidProbe bridge.

Start with:

- [survey](docs/survey.md)
- [goal](docs/goal.md)
- [architecture](docs/architecture.md)
- [capability matrix](docs/capability-matrix.md)
- [Agent interface comparison](docs/agent-interface-matrix.md)
- [roadmap](docs/roadmap.md)
- [verification](docs/testing.md)
- [comparative hotspot workflow](docs/comparative-hotspot-workflow.md)

Generated reports and outputs stay under .local/ and never enter Git.
