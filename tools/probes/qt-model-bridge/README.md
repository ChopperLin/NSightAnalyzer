# Qt model bridge probe

This directory contains an unsupported, version-pinned probing tool. It is not
a public NSightAnalyzer operation or a supported NVIDIA adapter. Its purpose is
to establish which facts can be recovered from a real `.ngfx-gputrace` before
designing a product interface.

Nsight Graphics opens and decodes the report. A startup `QGenericPlugin` then
reads the decoded public Qt item-model surfaces on the Viewer GUI thread. The
probe does not parse WRPV, call `ngfx-rpc`, inject a DLL, take screenshots, or
synthesize mouse/keyboard input.

## Build and install

```powershell
pwsh -File tools/probes/qt-model-bridge/build-and-install.ps1
```

The script resolves CMake (from `PATH` or a Visual Studio 2022 installation)
and Qt 6.8.1, builds `solidprobe.dll`, backs up the currently installed plugin
under a Viewer-specific `.local/bridge-backups/` directory, installs into the selected exact Viewer's
`plugins/generic/`, and verifies the installed file hash.
It reads the same `viewer-host-targets.json` embedded by the CLI. If future exact releases share the
same file-level product version and a nonstandard install path, pass `-TargetProductVersion` to select
the certified row explicitly.

It refuses to install when `kPluginVersion` in `solid_probe_plugin.cpp` differs
from `ViewerProbeRunner.ExpectedBridgeVersion`, because that mismatch is
otherwise only detectable at runtime. Bump both together.

Installing writes under `Program Files` and needs an elevated session; the
script checks first and says so rather than failing part way through. Use
`-SkipInstall` to build only, `-WhatIf` to preview, and `-Rollback` to restore
the most recent backup.

## Verified hosts

| Viewer application | Build/SKU | Qt runtime |
|---|---|---|
| 2026.2.0.0 | 37991608, public-release | 6.8.1 |
| 2026.3.1.0 | 38722833, public-release | 6.10.2 |

The plugin is compiled with Qt 6.8.1 and MSVC 2022 x64. Qt's
[plugin deployment rules](https://doc.qt.io/qt-6/deployment-plugins.html) permit a plugin built with
an older Qt 6 minor to load in a newer Qt 6 runtime; the exact 6.8.1-to-6.10.2 pairing is additionally
closed by the real Viewer tests. Probe schema implementation is `probe-0.52`; committed Probe 0.44
outputs remain the frozen semantic oracle.

The downloaded Qt Core, Gui, and Widgets release DLLs were byte-identical to
the DLLs shipped by this Nsight installation. The Qt plugin entry point and
model/selection/stack APIs are public Qt APIs. Nsight object names, model
classes, roles, and lifecycle are implementation details. Re-probe every new
Nsight build.

## Proven facts

The following paths were verified against generated and real reports:

- Complete Event List hierarchy: pass/marker names, queue and command-list
  nesting, action text, event ranges, and CPU/GPU timing columns.
- Exact Event pagination: a 654 MB, 30-frame report contained 306,576 nodes.
  Requesting `offset=306570, limit=10` returned the final six nodes and
  `hasMore=false`, ending in event 276761 `Present`.
- Stable Event selectors by path, preorder ordinal, event range, or description
  plus occurrence. Missing selectors return `event-target-not-found` after the
  loaded tree count stabilizes.
- 88 Warp Metrics tables (409 rows for the tested `GBufferPass`), including SM
  register occupancy, warp occupancy/stalls/latency, SysL2/VidL2 hit rate and
  throughput, VRAM bandwidth, memory traffic, queues, draw counts, and pipeline
  utilization. Header units, display values, full-precision Tooltip values,
  and metric descriptions are all recoverable.
- Warp Metrics are directly available after selecting a pass; Trace Compare is
  not required. Attached-view filters returned only `SM Register Occupancy`
  and `VidL2 Throughput` from the 88-table surface in a 73 KB result.
- Per-pass refresh. Selecting `DeferredLightingPass` (events 3507-3555) changed
  all five requested Register/L2/VRAM tables and produced, among other values,
  89.2% Pixel Register Allocation, 73.8% VidL2 hit rate, and 20.2% VRAM read
  bandwidth.
- Per-frame refresh on the real 30-frame report. Frame 0 and frame 29 each
  returned 88 tables/409 rows for their own `GBufferPass`; 258 of 376 unique
  logical metrics changed. The selections retained distinct event ranges and
  frame values, so repeated marker names did not alias across frames.
- Structured Event Parameters. Selecting draw event 1661 returned typed fields
  for `DrawIndexedInstanced`, including `InstanceCount=94206` and
  `StartInstanceLocation=262176`.
- Shader inventory and static/dynamic properties from `SampleItemTreeModel`:
  type, entry point, shader hash, pipeline name (when grouped by pipeline),
  samples, warps, registers, shared memory, CTA dimensions, live registers,
  instruction mix, dependency-attributed samples, and stall columns. The
  30-frame report's frame-0 `GBufferPass` exposed 1,020 shader hashes, 133 with
  PC samples, totaling 27,432 samples.
- Shader-hash drilldown into PC sampling. Pixel shader
  `0xc7096045a5804ec3` exposed 3,592 samples, `# Warp=24`, `# Reg=30`, 14 live
  registers, 544 static instructions, and 1,501 Long Scoreboard samples.
- The shader row's instruction-mix vector is shader-specific. The separate
  `InstructionMixModel` is range aggregate, not shader-specific: every shader
  selection under the tested `GBufferPass` retained 39,508 samples, 130,656
  executed instructions, and 56 categories; `DeferredLightingPass` instead
  produced 42,801 samples, 93,400 instructions, and 52 categories.
- DXIL/source-correlation export for the shader above returned 480 DXIL rows
  with per-line samples, stalls, latency, instruction mix, dependency samples,
  and live registers. The driver also supplied 242 SASS addresses and a
  SASS-to-DXIL line table, allowing address association without screenshots.
- Trace Analysis exposes its generated rules/metrics as Qt models; the tested
  30-frame report exposed per-frame analysis rather than only an aggregate
  screenshot. The Viewer's built-in counter export also produced 226 ranges by
  647 raw columns on the single-frame report.

On the generated validation trace, 18 values across two markers matched the
official `GPUTRACE_REGIMES.xls` export exactly, covering register allocation,
registers per SM, SysL2 hit rate, and VRAM read/write values.

## Required dependency order

Nsight lazily initializes several profiler models. Read them in this order:

1. Wait for the Event List.
2. Select the requested pass/action and wait for Warp Metrics to stabilize.
3. Wait for Shader Profiler models, then select a shader by exact hash.
4. Activate one requested profiler panel through `QStackedWidget` and wait.
5. Export only the projection exposed by the attached view.

Do not bulk-read every discovered model immediately after report load. In
particular, `InstructionMixModel` reports a 15-column source model while its
three attached views expose a five-column proxy. Columns 5-14 are internal and
crashed the 2026.2 Viewer when queried for these rows. The probe now detects
the five-column view projection and defaults to columns 0-4. Explicit
`MODEL_COLUMN_POLICY=all` is a dangerous probing override.

Generic model and metric `itemData()` enumeration is also opt-in. Display and
high-precision Tooltip roles contain the useful facts; eager enumeration of
custom UI roles caused unstable Viewer shutdown and revealed no metric IDs.

Large reports also need a selection barrier. The 30-frame report initially
allowed selection when only one of 88 metric tables existed, which raced the
Viewer's lazy initialization and spawned CrashReporter. Probe 0.44 can require
a minimum table count and stable polls before changing the selected event; the
same run then completed twice with zero CrashReporters. When a model view has
no current row, the bridge uses that empty state as the preflight baseline;
waiting for a details provider before selecting its producer row would
otherwise deadlock shader-source extraction.

Selection-metrics runs now derive their bounded poll limit from
`METRIC_SETTLE_MAX_POLLS` (minimum 120 polls). This prevents a slow asynchronous
Trace Analysis build from being cut off by the bridge's former fixed 120-poll
outer limit; the caller's process timeout remains the final bound.

## Build

```powershell
cmake -S tools/probes/qt-model-bridge `
  -B .local/build/qt-model-bridge `
  -DCMAKE_PREFIX_PATH=.local/Qt/6.8.1/msvc2022_64
cmake --build .local/build/qt-model-bridge --config Release --parallel
```

Generated binaries and report-derived JSON stay under `.local/` or a
caller-provided local artifact directory.

The admitted Viewers reset Qt library paths to their own `Plugins` directory. This
machine-local experiment exposes the build's `out/generic` directory under the
Viewer installation's `Plugins/generic` path. Do not commit or redistribute
that installation hook. The harness verifies that `solidprobe.dll` is present
and accepts only the two exact Viewer compatibility entries; it does not install the hook.

## Repeatable harness

`invoke_viewer_probe.ps1` is a low-level test harness, not a product CLI. It
verifies the pinned installation, launches the Viewer, passes arbitrary probe
settings, bounds runtime, validates request ID/schema/freshness, detects a
CrashReporter spawned by that run, and emits one `NsightSolidProbeRunV1` JSON
document.

```powershell
tools/probes/qt-model-bridge/invoke_viewer_probe.ps1 `
  -ReportPath .local/sample.ngfx-gputrace `
  -OutputPath .local/results/event-page.json `
  -Mode event-export `
  -Settings @{ EVENT_OFFSET = 1000; EVENT_LIMIT = 100 }
```

The Viewer must be launched normally. `Start-Process -WindowStyle Hidden`
reproducibly caused an early Viewer crash on the 50 MB report, before the Event
List loaded. The harness intentionally does not hide the target UI process.

The product transport uses the same bridge in a restricted session mode: one normally launched
Viewer per exact trace snapshot, serialized self-contained requests, idle/explicit close, and crash
rebuild. Nsight also keeps one Viewer-owned CrashReporter pipe guardian alive during that session;
the product records that exact child as baseline and rejects only an additional or replacement
CrashReporter.

## Probe modes

- `heartbeat`
- `event-discovery`
- `event-export`
- `metrics-discovery`
- `metrics-export`
- `model-catalog`
- `model-export`
- `selection-metrics-export`

Configuration is deliberately environment-driven and low-level. The harness
maps each `Settings` key to `NSIGHT_SOLID_PROBE_<KEY>`.

Common settings:

- `OUTPUT`, `MODE`, `REQUEST_ID`, `REPORT_ID`
- `QUIT_WHEN_READY=1`
- `MAX_DEPTH` (default 24, maximum 64)
- `POLL_INTERVAL_MS` (10-500ms) changes only the probe's bounded observation
  cadence. Product operations admit it explicitly and use fixture-proven values:
  range metrics use 100ms and range shaders use 200ms. A shorter shader cadence
  was rejected because a cold Viewer exposed the stable static inventory before
  dynamic sample counts arrived.

The product session accepts one explicit cache policy, `range-shaders-v1`. It retains only a
complete, stable, non-truncated shader-model response in bounded Viewer-process memory (four entries,
64 MiB). The key closes report/version/schema/mode and the complete settings object. A hit still
resolves and selects the exact requested event and verifies that current selection before rebinding
the response request id. Other selection-metrics requests, including Instruction Mix and source
activation, cannot opt into this policy.

Event export and selectors:

- `EVENT_OFFSET`, `EVENT_LIMIT`, `EVENT_INCLUDE_ITEM_DATA`
- `EVENT_PATH`
- `EVENT_RANGE`
- `EVENT_ORDINAL`
- `EVENT_MATCH`, `EVENT_MATCH_MODE=exact|contains`
- `EVENT_OCCURRENCE`, `EVENT_SEARCH_LIMIT`
- `EVENT_GRAIN_COLUMN`, `EVENT_GRAIN_PREFIXES`,
  `EVENT_GRAIN_SUBSTRINGS`, `EVENT_GRAIN_MODE=include|exclude` apply an
  independent range-kind predicate alongside a caller name filter
- `EVENT_SORT_DURATION_COLUMN` orders the matched subsequence by parsed Viewer
  duration descending, with true preorder ordinal as the tie-breaker
- `EVENT_WITHIN_ORDINAL` restricts matches to strict descendants and returns
  the exact resolved scope plus ancestors

Selector precedence is path, range, ordinal, then description.

Warp Metrics:

- `METRIC_TABLE_MATCH`: semicolon-separated table names
- `METRIC_TABLE_MATCH_MODE=exact|contains`
- `METRIC_OFFSET`, `METRIC_LIMIT`
- `METRIC_INCLUDE_ITEM_DATA=1` for explicit role probing only
- `METRIC_CATALOG_ONLY=1` returns stable table headers and row/column counts
  without metric cells
- `METRICS_MIN_POLL` (safe default 10)
- `SELECTION_BASELINE_METRIC_MIN_COUNT` and
  `SELECTION_BASELINE_METRIC_STABLE_MIN_POLLS` gate event selection on a stable
  metric surface (the verified 30-frame profile used `80` and `2`)

Model discovery/export:

- `MODEL_CLASS_MATCH`, `MODEL_OBJECT_MATCH`
- `MODEL_INSTANCE_ORDINAL` to isolate one matching class/object instance
- `MODEL_MATCH_MODE=exact|contains`
- `MODEL_ATTACHED_VIEW_CLASS_MATCH`, `MODEL_ATTACHED_VIEW_OBJECT_MATCH`, and
  `MODEL_REQUIRE_VISIBLE_VIEW=1` for precise on-demand table selection
- `MODEL_OFFSET`, `MODEL_LIMIT`, `MODEL_COLUMNS`
- `MODEL_REQUIRED_MIN_COUNT` to prevent an empty filtered result from being
  treated as stable while an asynchronous Viewer action is still producing it
- `EXTERNAL_KILL_ON_TIMEOUT=1` leaves a timed-out asynchronous action alive for
  the bounded harness to terminate, avoiding an unsafe Viewer shutdown while
  background analysis work is still running
- `MODEL_COLUMN_POLICY=all` to override safe view projection
- `MODEL_FLAT=1`, `MODEL_STRUCTURE_ONLY=1`
- `MODEL_VALUE_MODE=normal|string|type`
- `MODEL_INCLUDE_HEADERS=0|1`, `MODEL_INCLUDE_ITEM_DATA=1`
- `MODEL_CAPTURE_BASELINE=1` is an unsafe opt-in; default is off

Shader row selection and downstream panel activation:

- `MODEL_SELECT_VIEW_OBJECT` (default `SampleTreeView`)
- `MODEL_SELECT_PATH` or `MODEL_SELECT_ROW` for session-local probing
- `MODEL_SELECT_MATCH`, `MODEL_SELECT_COLUMN`,
  `MODEL_SELECT_MATCH_MODE`, `MODEL_SELECT_OCCURRENCE`
- `MODEL_SELECT_TRIGGER=activated|clicked|doubleClicked|mouseClick` to reproduce a
  view-level row action after changing the current selection
- `MODEL_SELECT_TRIGGER_COLUMN` to emit that action on a link-bearing sibling
  cell such as a shader's File Name column
- `MODEL_SELECT_TRIGGER_X_OFFSET` to target link text near the left edge of a
  view cell instead of its center
- `MODEL_SELECT_PREPARE_PANEL` and
  `MODEL_SELECT_PREPARE_PANEL_SETTLE_MIN_POLLS` to make a hidden selection view
  visible before emitting a view-level action
- `MODEL_SELECT_DEFER_PROVIDER_VERIFY_UNTIL_COMBO=1` when the target details
  provider is created by a subsequent combo selection
- `MODEL_SELECT_INVOKE_CLASS` and `MODEL_SELECT_INVOKE_METHOD` for an explicit,
  version-pinned Qt meta-object call using the selected source-model item's
  internal pointer (for example `SummaryPage.LoadSource`)
- One-parameter internal-pointer calls additionally require
  `MODEL_SELECT_INVOKE_ALLOW_UNSAFE_POINTER=1`; the default only permits safer
  no-argument slots such as `SummaryPage.SelectionChanged`
- `ACTIVATE_PANEL`, for example `FlatTabPanel_Instruction Mix`
- `ACTIVATE_PANEL_VIA_BUTTON=1` for panels whose provider initializes only
  through the corresponding `FlatTabButton_*` action

Shader hash matching is stable across runs; row/path identifiers can change
when Nsight asynchronously sorts the shader table.

Controlled UI actions used only to instantiate lazy data surfaces:

- `ACTION_TRIGGER_TEXT_MATCH`, `ACTION_MATCH_MODE`,
  `ACTION_TRIGGER_OCCURRENCE`, `ACTION_TRIGGER_ASYNC=1`
- `INVOKE_CLASS_MATCH`, `INVOKE_OBJECT_MATCH`, `INVOKE_TEXT_MATCH`, ancestry
  filters, `INVOKE_OCCURRENCE`, and zero-argument `INVOKE_METHOD`
- `COMBO_*`, `TAB_*`, and `OBJECT_*` filters for cataloging a specific lazy
  panel without screen coordinates
- `COMBO_SELECT_FROM_MODEL_SELECTION=shader-source` derives the SourceDetails
  combo identity from the selected shader name and its pipeline parent
- `CLOSE_MODAL_BEFORE_QUIT=1` closes probe-opened modal windows before Viewer
  shutdown

## Boundary

The `.ngfx-gputrace` must be opened in the matching Viewer so this plugin is
loaded into the decoding process. This differs from RazorProbe's supported
offline API shape. No discovered public Nsight Graphics SDK currently exposes
all of these decoded report models. Nsight Perf SDK remains useful for live
counter collection and instrumentation, but it is not the decoder used by
this existing-report probe.

The tested Standard Viewer does not expose full SASS opcode text. Its own
`ShaderProfilerPlugin.dll` states that SASS instructions/disassembly require
the Pro edition. The probe still recovers the PC address line table and all
DXIL-correlated samples, but it does not bypass that license boundary.

HLSL and function-level hotspots require matching shader PDBs. For
`0xc7096045a5804ec3`, the Viewer requested
`a20651a4fd7e9710dfaaa90fdfd2aed0.pdb`; it was not present, while the Viewer
reported successful SASS debug-info and SASS-to-IL line-table loading. Empty
HLSL/function models in that case are an explicit input-data limitation.

PC sampling is not reliably scoped to one draw. Selecting draw 1661 expanded
the shader-tree sample total from the enclosing marker's 39,508 to 494,565;
the same shader retained 3,592 samples but changed from 9.09% to 0.73% of the
selection. Draw selection remains useful for API parameters and pipeline
membership, while pass/marker ranges are the supported attribution grain.

`RangesView`/`RangesModel` symbols exist in WarpViz, but no such model was
instantiated by the normal GPU Trace document, Trace Analysis, or a completed
Trace Compare operation. Timeline action/range data is nevertheless available
through the complete Event List hierarchy. Do not keep probing this private
view unless a future Viewer build exposes it in the GPU Trace UI.

A GPU Trace report contains performance and execution metadata, not a Graphics
Capture's replayable render targets and resource contents. It can diagnose
performance and correlate suspicious work, but it cannot independently prove
a pixel-level visual-correctness issue.

This mechanism remains a probe until a supported NVIDIA surface or a second
independent implementation exists. Product operations must not silently depend
on it.
