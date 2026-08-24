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

The reports remain at user/local paths; tests receive them explicitly. Product defaults never scan
Downloads or other broad directories.

## Fixed facts

- Viewer 2026.2 build 37991608, Qt 6.8.1, probe 0.44.
- 30-frame event count 306,576.
- tested pass surface 88 metric tables and 409 rows.
- frame comparison 258 changed out of 376 logical metrics.
- frame-0 GBuffer shader inventory 1,020 hashes, 133 sampled, 27,432 samples.
- focused c709 shader and source values listed in testing.md.
- counter export: 226 ranges, 646 counter columns plus the range-name column; GBuffer pixel-register
  allocation is 62.7392%, closing to the Warp Metrics precise value 62.739219%.

These are fixture facts, not constants for every trace.

## Future corpus expansion

Additional traces are added only when real dogfood requires a missing variant: Vulkan, async compute,
ray tracing, multiple queues, Pro Viewer SASS, or a trace with matching shader PDBs. A hypothetical
variant does not justify a new atom or abstraction.
