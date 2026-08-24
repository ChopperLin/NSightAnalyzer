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

Probe 0.44 and the current product atoms have recovered and validated:

- complete event/pass/marker/action navigation;
- range-scoped register, L2, VRAM, occupancy, stall, latency, queue, draw, and pipeline metrics;
- per-frame refresh and repeated-marker disambiguation;
- event parameters;
- shader inventory, samples, warps, static register/instruction data, stalls, and instruction mix;
- DXIL line hotspots and SASS-address-to-DXIL correlation;
- Trace Analysis models and the Viewer's raw counter export.

The committed probing implementation under tools/probes/qt-model-bridge is the legacy oracle.
The atom layer currently exposes 11 operations: capability/identity, events, event parameters,
range metrics, range instruction mix, range shaders, shader source, Trace Analysis, and raw-counter
catalog/value retrieval. Deterministic wrappers and Agent diagnosis remain intentionally deferred.

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

Generated reports and outputs stay under .local/ and never enter Git.
