# Agent interface comparison with RenderDoc MCP

## Purpose

This matrix compares Agent-callable semantic operations, not internal API or tool counts. Its job is
to identify which useful RenderDoc MCP interaction patterns belong in the existing GPU Trace product
scope and which depend on Graphics Capture replay and must stay out of the NSightAnalyzer roadmap.

The RenderDoc reference surface was reviewed on 2026-08-27 from:

- [Linkingooo/renderdoc-mcp](https://github.com/Linkingooo/renderdoc-mcp), including its
  [tool-design notes](https://github.com/Linkingooo/renderdoc-mcp/blob/master/renderdoc-mcp-tool-design.md);
- [halby24/RenderDocMCP](https://github.com/halby24/RenderDocMCP).

The NSightAnalyzer source of truth remains the machine-readable `capabilities` result. This document
groups operations by user intent and does not create a second operation registry.

## Status

- **current**: a public operation already serves the intent with verified GPU Trace facts;
- **partial**: the facts exist, but the public request or result shape makes the Agent fetch too much,
  guess an identity, or compose avoidable calls;
- **build**: the missing shape can be implemented from facts already proven by the oracle;
- **probe**: useful for GPU Trace only if a real decoded Viewer model proves the fact first;
- **unavailable**: a real report/build audit proved that the decoded semantic surface is absent;
- **excluded**: requires Graphics Capture/replay rather than an existing GPU Trace report.

## Matrix

| Agent intent | RenderDoc MCP examples | NSightAnalyzer today | Status | Decision |
|---|---|---|---|---|
| Open/close and report status | `open_capture`, `close_capture`, `get_capture_status` | automatic trace open, `trace.info`, `viewer-session.close` | current | Keep automatic open; session reuse remains transport only. |
| Decoder and trace identity | `get_capture_info` | `trace.info`, `capabilities` | current | Keep the stronger version/build/bridge provenance. |
| Compact frame overview | `get_frame_overview`, `get_frame_summary` | `find-ranges` plus `trace.info` | current | Use bounded ordered candidates instead of a second large overview payload. |
| Browse the action/range tree | `list_actions`, `get_draw_calls` | `trace.events`, `trace.outline` | current | Keep exact EventKey identity, hierarchy and paging. |
| Search actions by name | `search_actions` | `find-events` | current | Return bounded exact EventKeys for any API event/range substring, optionally under one exact ancestor. |
| Find interesting draws/ranges | `find_draws` | `find-ranges` | current | Bounded name/grain/scope filtering and Viewer-duration ordering over objective outline facts. |
| Inspect one API command | `get_action`, `get_draw_call_details` | `trace.event-parameters` | current | Keep structured command parameters; do not pretend they are full pipeline state. |
| Rank expensive passes/actions | `get_pass_timing`, `get_action_timings` | `find-ranges`, `trace.outline`, `trace.analysis` | current | `find-ranges` returns bounded Viewer-duration-ranked candidates. |
| Discover available metrics | no consistent dedicated operation | `trace.range-metric-catalog` | current | Returns table/column identity and units without metric values. |
| Read pass metrics | timing/performance helpers | `trace.range-metrics` | current | Keep exact table filters, long-form values, occurrences and precise tooltips. |
| List range shaders | shaders embedded in draw/pipeline results | `trace.range-shaders` | current | Keep the complete paged inventory. |
| Inspect one exact shader | `get_shader_info` | `trace.shader-profile` | current | Singular result keyed by range EventKey plus exact stage/hash/occurrence. |
| Read shader source/disassembly | `disassemble_shader`, `get_shader_source` | `trace.shader-source` | current | Keep explicit PDB, SASS-debug and SKU availability states. |
| Read per-line PC hotspots | no direct equivalent | `trace.shader-source` | current | Keep samples, stalls, dependency samples and live registers as a GPU Trace strength. |
| Read shader/range instruction mix | no direct PC-sampling equivalent | `trace.range-shaders`, `trace.range-instruction-mix` | current | Keep shader and range attribution as distinct fact families. |
| Read Viewer analysis findings | performance-analysis helpers | `trace.analysis` | current | Return Viewer facts only; diagnosis remains in the Skill. |
| Read raw GPU counters | timing/counter helpers | `trace.counter-catalog`, `trace.range-counters` | current | Keep exact seed scope and validated local export evidence. |
| Inspect full event execution state | `get_pipeline_state`, `get_draw_call_state` | command parameters and range-level shader inventory only | unavailable | The tested report/build exposes no exact state model; do not reconstruct one from command history or profiler grouping. |
| Inspect shader/resource bindings | `get_shader_bindings`, shader reflection | no proven product fact | probe | Probe independently after execution-state evidence; do not reconstruct state from pointer-like command text. |
| Browse resource metadata | `list_textures`, `list_buffers`, `list_resources`, `get_texture_info` | no proven product fact | probe | In scope only for metadata materialized by the GPU Trace Viewer. |
| Trace resource read/write usage | `get_resource_usage` | no reliable resource identity/usage atom | probe | Require stable report resource identity and a real usage model before contract design. |
| Compare two scopes | `diff_draw_calls` | `compare-ranges`, `compare-frame-timing` | partial | Performance deltas are current; state diff waits for execution-state facts. |
| Retrieve texture/buffer contents | `get_texture_data`, `get_buffer_data`, texture export | none | excluded | Requires Graphics Capture replayable resource contents. |
| Pixel inspection/history/debug | `pick_pixel`, `pixel_history`, `debug_shader_at_pixel` | none | excluded | Requires replay and is outside GPU Trace. |
| Mesh and post-VS data | `get_post_vs_data`, `export_mesh` | none | excluded | Requires Graphics Capture replay. |
| Render-target snapshots and pixel diagnostics | `save_render_target`, region sampling, NaN scans | none | excluded | Do not add screenshot scraping or Graphics Capture to the product path. |
| Automatic bottleneck diagnosis | `analyze_overdraw`, `analyze_bandwidth`, diagnostic tools | objective facts plus future Agent Skill | intentionally separate | Causality, priority, confidence and recommendations stay in the Skill. |

## Closed in `probe-0.51`

### `find-ranges` wrapper

Uses the already verified `trace.outline` fact family. The request is bounded and self-contained:
optional exact ancestor EventKey, grain, name substring, ordering, cursor and limit. The compact result
contains EventKey, name, grain, Viewer duration and ancestor context. It returns candidates rather than
requiring the caller to know an occurrence in advance. It does not label a candidate as a bottleneck.

### `trace.range-metric-catalog/v1` atom

For one exact pass/marker EventKey, returns stable table occurrence, row count, column occurrence,
column unit and availability. Do not include metric values. The 88-table real fixture and existing
metric oracle are the acceptance source. The bridge emits header-only exports with no metric cell
payload.

### `trace.shader-profile/v1` atom

For one exact range EventKey and ShaderKey, returns one shader fact rather than a one-item page. The
request includes stage, normalized hash and hash occurrence so a duplicate hash cannot match two
stages. It reuses the verified range-shader model and its Viewer-process cache; source rows stay in the
separate `trace.shader-source` atom.

## Event execution-context audit

### Event execution context

On the real single-frame report, selecting exact `ClearRenderTargetView` event 1774 changed only the
Instruction Mix pair among 380 decoded models. Resource, descriptor and current-target actions were
disabled. Selecting draw 1661 and its enclosing GBufferPass produced the same set of 506 Shader
Pipelines roots; only attribution values and asynchronous order differed. Therefore this build has
no proven exact PSO, topology, viewport, render/depth-target or binding state fact. A future report
captured with additional metadata may reopen the binding/resource rows independently, but the
current product does not publish a synthetic execution-state operation.

## Closed after the `probe-0.51` bridge

### `find-events` wrapper

Reuses the verified bridge-side name and exact-ancestor predicates. It searches any Event List row,
not only ranges, and returns one bounded preorder page plus exact EventKeys and shared ancestor
context. The single-frame report has 52 `ClearRenderTargetView` matches; a warm scoped query returned
the seven strict descendants of ordinal 1680 in 2.08 s, one atom call and 3.7 KB.

## Acceptance for new public operations

- Preserve exact trace, EventKey/ShaderKey, decoder and bridge provenance.
- Return compact bounded collections with stable ordering and explicit truncation.
- Never depend on a previous GUI selection; re-establish and verify the requested scope.
- Close every field against the frozen probe and a real or sanitized fixture.
- Record elapsed time, returned byte size and call count in interactive dogfood.
- Expose no Qt names, widget state, generic model access or diagnostic judgment.
