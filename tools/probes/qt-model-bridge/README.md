# Qt model bridge probe

This is an unsupported, version-pinned probing tool, not a product adapter or
public NSightAnalyzer operation. It lets Nsight Graphics decode a GPU Trace and
then reads the resulting Qt item models in process. It does not parse WRPV,
call `ngfx-rpc`, inject a DLL, take screenshots, or synthesize mouse/keyboard
input.

## Verified host

- Nsight Graphics 2026.2.0, build 37991608
- Qt 6.8.1, MSVC 2022 x64
- The downloaded Qt Core, Gui, and Widgets release DLLs were byte-identical to
  the DLLs shipped by this Nsight installation.

The plugin entry point (`QGenericPlugin`) and model access
(`QAbstractItemModel`) are public Qt APIs. The Nsight object names and model
layout are implementation details, so every new Nsight build must be probed
again before use.

## Proved capabilities

- Export the complete Event List hierarchy, including marker/pass names,
  action parameters, event ranges, and CPU/GPU timing columns.
- Discover and export 88 Warp Metrics tables, including register occupancy,
  warp occupancy, L2/SysL2/VidL2, VRAM, and throughput tables.
- Select an exact Event List marker through `QItemSelectionModel`, wait for the
  Warp Metrics models to refresh, and export the selected range.
- Preserve both formatted display values and higher-precision tooltip values.

On the generated validation trace, 18 values across two markers matched the
official `GPUTRACE_REGIMES.xls` export (register allocation, registers per SM,
SysL2 hit rate, and VRAM read/write metrics). Bounded tests also succeeded for
an approximately 50 MB report and an approximately 654 MB multi-frame report.
All tested Viewer runs exited naturally with code zero and produced no new
crash-report process.

## Build

```powershell
cmake -S tools/probes/qt-model-bridge `
  -B .local/build/qt-model-bridge `
  -DCMAKE_PREFIX_PATH=.local/Qt/6.8.1/msvc2022_64
cmake --build .local/build/qt-model-bridge --config Release --parallel
```

Generated binaries and extracted report data must stay under `.local/` or a
caller-provided local artifact directory.

## Probe modes

The Viewer is launched with `-plugin SolidProbe`. Configuration is currently
deliberately low-level and environment-driven:

- `heartbeat`
- `event-discovery`
- `event-export`
- `metrics-discovery`
- `metrics-export`
- `selection-metrics-export`

Important variables are `NSIGHT_SOLID_PROBE_OUTPUT`,
`NSIGHT_SOLID_PROBE_EVENT_MATCH`, `NSIGHT_SOLID_PROBE_MAX_NODES`,
`NSIGHT_SOLID_PROBE_EVENT_EXPORT_MIN_POLL`, and
`NSIGHT_SOLID_PROBE_QUIT_WHEN_READY`.

Nsight 2026.2 resets Qt library paths to its own `Plugins` directory. The
machine-local experiment therefore exposed the build's `out/generic` directory
under the Nsight installation's `Plugins/generic` path. Do not commit or
redistribute that installation hook; a future wrapper must provision and
remove it explicitly and verify the exact Nsight build first.
