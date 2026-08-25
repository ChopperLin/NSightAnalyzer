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

The frozen Probe 0.44 oracle and the current `probe-0.47` product bridge have recovered and
validated:

- complete event/pass/marker/action navigation;
- range-scoped register, L2, VRAM, occupancy, stall, latency, queue, draw, and pipeline metrics;
- per-frame refresh and repeated-marker disambiguation;
- event parameters;
- shader inventory, samples, warps, static register/instruction data, stalls, and instruction mix;
- DXIL line hotspots and SASS-address-to-DXIL correlation;
- Trace Analysis models and the Viewer's raw counter export.

The committed probing implementation under tools/probes/qt-model-bridge is the legacy oracle.
The atom layer exposes 12 operations: capability/identity, outline, events, event parameters, range
metrics, range instruction mix, range shaders, shader source, Trace Analysis, and raw-counter
catalog/value retrieval. The deterministic wrapper slice adds `resolve-event`, `inspect-pass`,
`compare-ranges`, and the same-trace `compare-frame-timing` timeline decomposition. Agent diagnosis
remains intentionally above those wrappers.

Start an investigation with `trace.outline`: it returns the range-grain skeleton - every pass,
marker and command-list range with its timing - in one call, instead of paging the full event tree.
Its preorder ordinals are the same EventKeys the scoped atoms take, so a returned ordinal can be
passed straight to `inspect-pass` or `trace.range-metrics`.

`--help`, or no arguments at all, returns the same catalog as `capabilities`, including a
machine-readable parameter definition for every operation: value kind, required or optional,
defaults, bounds, allowed values, and mutually exclusive selector groups. Occurrence arguments are
zero-based.

Product atoms reuse one normally launched Viewer process per exact trace snapshot. The normal
CrashReporter pipe guardian is recorded as session baseline rather than treated as a crash; Viewer
exit or any additional/replacement guardian invalidates the session, and the next call rebuilds it.

```powershell
dotnet build .\NSightAnalyzer.slnx -c Release
dotnet .\src\NsightAnalyzer\bin\Release\net9.0-windows\nsight-analyzer.dll `
  capabilities --compact
```

Start with:

- [survey](docs/survey.md)
- [goal](docs/goal.md)
- [architecture](docs/architecture.md)
- [capability matrix](docs/capability-matrix.md)
- [roadmap](docs/roadmap.md)
- [verification](docs/testing.md)
- [comparative hotspot workflow](docs/comparative-hotspot-workflow.md)

Generated reports and outputs stay under .local/ and never enter Git.
