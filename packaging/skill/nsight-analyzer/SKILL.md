---
name: nsight-analyzer
description: Inspect existing NVIDIA Nsight Graphics GPU Trace reports (.ngfx-gputrace), locate expensive passes, retrieve metrics and shader evidence, and compare performance scopes. Use for GPU Trace performance investigations; exclude Graphics Capture, live collection, Nsight Systems and Compute.
---

# NSight Analyzer

Run the adjacent `nsight-analyzer.exe`. Requires Windows x64, .NET 9, Nsight Graphics Viewer 2026.2 build 37991608 and its matching SolidProbe bridge. This is an unsupported, version-pinned Viewer adapter.

Choose an absolute task workspace and pass `--workspace <path>` on every report
operation and session close. This keeps sessions and evidence under the same
`<workspace>/.local` when a tool changes its working directory. For installation
failures, run `doctor` and read [references/setup.md](references/setup.md).
For shell integration or JSON consumption, read [references/cli.md](references/cli.md).

## Choose the shortest evidence path

Default output is concise JSON: 20 items per page and Top 5 shaders with coverage.
No formatting flag is needed. Use `describe <operation>` or `<operation> --help`
for that operation's parameters. `capabilities` is a short directory; do not fetch
`capabilities --detail` just to learn one command.

| Request | Start with |
|---|---|
| Expensive passes or approximate marker name | `find-ranges` (marker grain by default); bound to the known frame with `--within-event-ordinal` |
| API command or draw/dispatch parameters | `find-events --name-contains …`, then `trace.event-parameters` |
| A named pass occurrence | `find-ranges --name-contains …`; use `resolve-event` for a known exact name and occurrence |
| Pass overview | `inspect-pass --event-ordinal …` |
| One metric family | `trace.range-metrics --table …`; discover unknown table names with `trace.range-metric-catalog` |
| A named metric or known metric source ordinal | `find-metrics --name-contains …` or `--source-ordinal …` in the exact range |
| One known shader | `trace.shader-profile` with returned stage, hash and occurrence |
| Sampled source hotspots | `find-source-hotspots` with returned ShaderKey and explicit `--view`; avoid paging through zero-sample rows |
| Timing alone | `inspect-pass --sections timing` or `compare-ranges --sections timing` |
| Several explicitly paired timing scopes | `compare-timings --pairs-file …`; resolve both sides first, never infer pairing from occurrence order |
| Target versus baseline | `compare-ranges`; metrics are the default section |
| Tree structure or frame/queue context | `trace.outline`, only when that hierarchy is needed |

Reuse returned identities. Do not repeat discovery when an exact EventKey or ShaderKey
is available, guess ordinals, or choose the first same-name occurrence when frame/parent
context differs. Occurrences are zero-based. Keep keys tied to their trace identity.

`inspect-pass` reads metrics and shaders by default. `compare-ranges` reads metrics
only. Select other facts with `--sections metrics,shaders` or `--sections instruction-mix`.
Every requested section must succeed; unrequested sections are omitted and identified
by `sections`. Instruction Mix is never an implicit prerequisite for a metric comparison.
Use `--sections timing` when durations answer the question without further metrics.

Comparison metric pages default to changed/unmatched facts, with `deltaFilter` and their own
page total. Overall matched/comparable counts still cover all selected metrics. Use
`--include-unchanged` only when unchanged deltas are needed; `--detail` controls descriptive fields.
`compare-timings` executes only the requested page; `statisticsBasis: currentPage`
does not summarize the entire pair file. Read [references/cli.md](references/cli.md)
for its UTF-8 pair-file format and explicit sample grouping.

Follow `nextCursor` only when more results are needed. A truncated page or shader
Top-N cannot support a claim about every item. Increase `--top-shaders` when coverage
is insufficient. Use `--detail` for metric descriptions or shader instruction vectors;
it does not disable paging. Prefer the singular shader profile for one shader.
Each stdout envelope is capped at 1 MiB. Because shader rows have variable width,
`trace.range-shaders` may return fewer rows than the requested `--limit` with warning
`runtime.response_page_reduced`; repeat the same request with its `nextCursor` until
that field is absent to close the full shader collection. This is normal byte-bounded
paging, not report corruption, and does not modify the report.
Metric pages can reuse an exact snapshot, but each request still establishes the
requested selection and verifies a fresh catalog before using it. Keep the same
trace identity, scope, table filters and workspace when continuing a page.
Source-hotspot coverage applies only to the explicitly selected view; correlated
DXIL, SASS and HLSL rows are not independent samples to add together.

## Interpret and answer

For diagnosis or comparison, read [references/analysis.md](references/analysis.md).
Report the answer first, then the few exact values that support it, their selected
scope, and any material evidence gap. Distinguish observation from hypothesis.
Mention source/version once; do not paste inventories, repeated provenance or raw JSON.

On failure, inspect `result.error.code` and `recovery`; read
[references/recovery.md](references/recovery.md) when recovery is needed. Do not
blindly increase timeouts or repeatedly request unavailable evidence.
Stop fetching once comparable facts support the answer or the missing evidence
cannot be obtained from this report. For multi-setting changes, separate capture
observations from build/configuration evidence; use the comparison rules in the analysis reference.

## Guardrails

- GPU Trace only: do not use this Skill for Graphics Capture, live collection, replay, Nsight Systems/Compute, Perf SDK, or Aftermath.
- Never parse the proprietary report container, scrape screenshots, or expose Qt/widget/model internals.
- Do not install or replace the Viewer bridge, change NVIDIA installation files, or elevate unless the user explicitly asks for that separate setup action.
- Preserve unknown, unavailable, unsupported, incomplete, and license/input-limited as distinct outcomes; none proves an empty result.
- Operations declaring `writesLocalArtifacts` may write only to their isolated local run directory and must preserve the caller's original report.
- When finished with each report, use `viewer-session.close <trace> --workspace <same-path>` (and the same `--viewer` override) to release its session.
