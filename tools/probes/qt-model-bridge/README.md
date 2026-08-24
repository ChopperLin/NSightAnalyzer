# Qt model bridge probe

This directory contains an unsupported, version-pinned probing tool. It is not
a public NSightAnalyzer operation or a supported NVIDIA adapter. Its purpose is
to establish which facts can be recovered from a real `.ngfx-gputrace` before
designing a product interface.

Nsight Graphics opens and decodes the report. A startup `QGenericPlugin` then
reads the decoded public Qt item-model surfaces on the Viewer GUI thread. The
probe does not parse WRPV, call `ngfx-rpc`, inject a DLL, take screenshots, or
synthesize mouse/keyboard input.

## Verified host

- Nsight Graphics 2026.2.0, build 37991608
- Viewer application version:
  `2026.2.0.0 (build 37991608) (public-release)`
- Qt 6.8.1, MSVC 2022 x64
- Probe schema implementation: `probe-0.5`

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
- 88 Warp Metrics tables, including SM register occupancy, warp occupancy,
  SysL2/VidL2 hit rate and throughput, VRAM bandwidth, memory traffic, and
  pipeline utilization.
- Per-pass refresh. Selecting `DeferredLightingPass` (events 3507-3555) changed
  all five requested Register/L2/VRAM tables and produced, among other values,
  89.2% Pixel Register Allocation, 73.8% VidL2 hit rate, and 20.2% VRAM read
  bandwidth.
- Structured Event Parameters. Selecting draw event 1661 returned typed fields
  for `DrawIndexedInstanced`, including `InstanceCount=94206` and
  `StartInstanceLocation=262176`.
- Shader inventory and static properties from `SampleItemTreeModel`: type,
  entry point, shader hash, samples, warps, registers, shared memory, CTA
  dimensions, live registers, instruction mix, and stall columns. One real
  report exposed 676 shader rows; 469 had register values.
- Shader-hash drilldown into PC sampling. Pixel shader
  `0x664f0abcd2665646` exposed `# Warp=6`, `# Reg=96`, shared-memory range
  `300-2400`, and 56 Instruction Mix rows.
- Instruction Mix raw values: `Pipe`, `Family`, `Operation`, a 17-element stall
  sample vector, and instruction count. Tooltip values preserve totals,
  percentages, and stall labels. For the shader above, FP32 FMA accounted for
  61,179 instructions and 10.3K samples; Long Scoreboard was 42.04% of those
  samples.
- Explicit absence. A tested Pixel shader had Instruction Mix data but no
  correlated Hotspot rows, so source/function hotspot data is represented as
  unavailable for that selection rather than as a probing failure.

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

## Build

```powershell
cmake -S tools/probes/qt-model-bridge `
  -B .local/build/qt-model-bridge `
  -DCMAKE_PREFIX_PATH=.local/Qt/6.8.1/msvc2022_64
cmake --build .local/build/qt-model-bridge --config Release --parallel
```

Generated binaries and report-derived JSON stay under `.local/` or a
caller-provided local artifact directory.

Nsight 2026.2 resets Qt library paths to its own `Plugins` directory. This
machine-local experiment exposes the build's `out/generic` directory under the
Viewer installation's `Plugins/generic` path. Do not commit or redistribute
that installation hook. The harness verifies that `solidprobe.dll` is present
and refuses a different Viewer version/build; it does not install the hook.

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

Event export and selectors:

- `EVENT_OFFSET`, `EVENT_LIMIT`, `EVENT_INCLUDE_ITEM_DATA`
- `EVENT_PATH`
- `EVENT_RANGE`
- `EVENT_ORDINAL`
- `EVENT_MATCH`, `EVENT_MATCH_MODE=exact|contains`
- `EVENT_OCCURRENCE`, `EVENT_SEARCH_LIMIT`

Selector precedence is path, range, ordinal, then description.

Warp Metrics:

- `METRIC_TABLE_MATCH`: semicolon-separated table names
- `METRIC_TABLE_MATCH_MODE=exact|contains`
- `METRIC_OFFSET`, `METRIC_LIMIT`
- `METRIC_INCLUDE_ITEM_DATA=1` for explicit role probing only
- `METRICS_MIN_POLL` (safe default 10)

Model discovery/export:

- `MODEL_CLASS_MATCH`, `MODEL_OBJECT_MATCH`
- `MODEL_MATCH_MODE=exact|contains`
- `MODEL_OFFSET`, `MODEL_LIMIT`, `MODEL_COLUMNS`
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
- `ACTIVATE_PANEL`, for example `FlatTabPanel_Instruction Mix`

Shader hash matching is stable across runs; row/path identifiers can change
when Nsight asynchronously sorts the shader table.

## Boundary

The `.ngfx-gputrace` must be opened in the matching Viewer so this plugin is
loaded into the decoding process. This differs from RazorProbe's supported
offline API shape. No discovered public Nsight Graphics SDK currently exposes
all of these decoded report models. Nsight Perf SDK remains useful for live
counter collection and instrumentation, but it is not the decoder used by
this existing-report probe.

This mechanism remains a probe until a supported NVIDIA surface or a second
independent implementation exists. Product operations must not silently depend
on it.
