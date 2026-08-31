---
name: nsight-analyzer
description: Inspect existing NVIDIA Nsight Graphics GPU Trace reports with the version-pinned NSightAnalyzer CLI. Use for .ngfx-gputrace outline, event, range, metric, shader, and comparison questions.
---

# NSight Analyzer

Run the adjacent `nsight-analyzer.exe`. It supports existing GPU Trace reports through Nsight Graphics Viewer 2026.2 build 37991608 and the matching installed SolidProbe bridge; it is not an official offline NVIDIA report SDK.

## Workflow

1. Start with `capabilities --compact` when the requested operation or parameters are unfamiliar.
2. Use `trace.outline` for the range-grain skeleton, then reuse the exact returned event ordinal.
3. Prefer one exact atom or deterministic wrapper over exporting the full event tree.
4. Report the selected trace/range identity, decoder provenance, classified failure, and truncation state.

```powershell
& '<skill-dir>\nsight-analyzer.exe' capabilities --compact
& '<skill-dir>\nsight-analyzer.exe' trace.outline '<trace.ngfx-gputrace>' --limit 500 --compact
& '<skill-dir>\nsight-analyzer.exe' inspect-pass '<trace.ngfx-gputrace>' --event-ordinal 0 --compact
& '<skill-dir>\nsight-analyzer.exe' compare-ranges '<trace.ngfx-gputrace>' --event-ordinal 0 --baseline-event-ordinal 1 --compact
```

Occurrence indexes are zero-based. For exact parameters and mutually exclusive selector groups, read the machine-readable `capabilities` result instead of guessing.

## Guardrails

- GPU Trace only: do not use this Skill for Graphics Capture, live collection, replay, Nsight Systems/Compute, Perf SDK, or Aftermath.
- Never parse the proprietary report container, scrape screenshots, or expose Qt/widget/model internals.
- Do not install or replace the Viewer bridge, change NVIDIA installation files, or elevate unless the user explicitly asks for that separate setup action.
- Preserve unknown, unavailable, unsupported, incomplete, and license/input-limited as distinct outcomes; none proves an empty result.
- Operations declaring `writesLocalArtifacts` may write only to their isolated local run directory and must preserve the caller's original report.
