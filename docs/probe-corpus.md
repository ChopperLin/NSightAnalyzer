# Local GPU Trace corpus

Raw reports are ignored and never committed. Sanitized bridge JSON and expected semantic projections
may be committed only after proprietary names, paths, command lines, shader source, and machine data
are removed.

## Current corpus

| Role | Local artifact | Proven use |
|---|---|---|
| generated validation | local generated .ngfx-gputrace plus official counter export | exact 18-value metric closure |
| real single frame | Ananta_2026_08_11_11_33_36.ngfx-gputrace | pass metrics, event parameters, shader/source oracle |
| real 30 frame | pkg20260819_Nvidia-RTX-5070ti_...ngfx-gputrace | large paging, repeated frame markers, shader scale |
| Vulkan descriptor heap | vk_descriptor_heap_2026_08_24_01_54_26.ngfx-gputrace | bind/barrier input variant and binding/resource-model absence |

The reports remain at user/local paths; tests receive them explicitly. Product defaults never scan
Downloads or other broad directories.

## Fixed facts

- Viewer 2026.2 build 37991608 / Qt 6.8.1 and Viewer 2026.3.1 build 38722833 / Qt 6.10.2.
- Probe 0.44 remains the frozen 2026.2 semantic oracle; product bridge `probe-0.52` closes both hosts.
- 30-frame event count 306,576.
- tested pass surface: 2026.2 exposes 88 metric tables, 409 rows and 801 values; 2026.3.1
  preserves those tables and adds two warp-occupancy tables for 90 tables, 1,627 rows and 3,237 values.
- frame comparison 258 changed out of 376 logical metrics.
- frame-0 GBuffer shader inventory 1,020 hashes, 133 sampled, 27,432 samples.
- focused c709 shader and source values listed in testing.md.
- counter export: 226 ranges on both hosts; 2026.2 exposes 646 counter columns and 2026.3.1 exposes
  1,864, plus the range-name column. GBuffer pixel-register allocation is 62.7392% on both, closing
  to the Warp Metrics precise value 62.739219%.

These are fixture facts, not constants for every trace.

## Future corpus expansion

Additional traces are added only when real dogfood requires a missing variant: async compute, ray
tracing, multiple queues, Pro Viewer SASS, or a trace with matching shader PDBs. A hypothetical
variant does not justify a new atom or abstraction.
