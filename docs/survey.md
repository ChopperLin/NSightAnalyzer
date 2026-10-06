# GPU Trace probing survey

## Conclusion

For an existing .ngfx-gputrace, the useful performance facts are decoded inside the matching Nsight
Graphics Viewer. The documented Nsight Graphics SDK controls capture/trace production, and Nsight
Perf SDK collects live in-application range/periodic data; neither is the existing-report decoder
needed here.

The successful path is a version-pinned QGenericPlugin loaded by the Viewer. It reads decoded Qt
item models on the GUI thread. It does not parse the report container, call ngfx-rpc, inject an
arbitrary DLL, scrape screenshots, or synthesize coordinate-based input.

## Verified hosts

| Viewer application | Build/SKU | Qt runtime | Evidence |
|---|---|---|---|
| 2026.2.0.0 | 37991608, public-release | 6.8.1 | Probe 0.44 oracle and real corpus |
| 2026.3.1.0 | 38722833, public-release | 6.10.2 | `probe-0.52`, same real corpus and agent workflow |

On the single-frame fixture, 2026.3.1 preserved all 88 existing metric-table exports and all 675
semantic shader rows (39,508 samples), added two warp-occupancy tables, and added one explicit
`Unattributed Samples` summary row. The adapter must refuse any other build until re-probed.

## Proven data surface

Three report classes were used: a generated validation trace, a real single-frame business trace,
and a real 30-frame business trace.

Verified facts:

- full Event List hierarchy with marker/pass/action names, event ranges, queues, CPU/GPU timing and
  tree topology;
- exact event paging over 306,576 nodes in the 30-frame report;
- stable selection by event path, preorder ordinal, range, or name plus occurrence;
- 88 Warp Metrics tables and 409 rows for the tested GBufferPass;
- register-file allocation, occupancy, warp/stall/latency, SysL2/VidL2, VRAM, memory traffic, queues,
  draw counts, and pipeline utilization;
- precise Tooltip values, display values, units, table/column headers, and descriptions;
- per-pass and per-frame refresh, including 258 changed metrics out of 376 logical metrics between
  frame 0 and frame 29;
- typed DrawIndexedInstanced parameters;
- shader inventory with stage, name/hash, samples, warps, registers, shared memory, CTA dimensions,
  live registers, instruction mix, dependency samples, and stall columns;
- 1,020 shader hashes in the 30-frame frame-0 GBufferPass, 133 sampled, 27,432 total shader samples;
- focused shader 0xc7096045a5804ec3 with 3,592 samples, 24 warps, 30 static registers, 14 live
  registers, 544 static instructions, and 1,501 Long Scoreboard samples;
- 480 DXIL rows and 242 SASS address rows with per-line sample/stall/live-register correlation;
- range-level instruction mix and per-frame Trace Analysis models;
- built-in raw counter export with 226 ranges and 647 total columns (646 counters plus range name)
  on the single-frame report.

On the generated trace, 18 values across two markers exactly matched the official
GPUTRACE_REGIMES.xls export.

## Stability findings

The Viewer models are lazy and stateful. Safe extraction requires:

1. wait for the Event List;
2. wait for a sufficiently complete and stable Warp Metrics catalog;
3. set the exact event selection and verify the resulting row;
4. wait for selected metrics to stabilize;
5. initialize only the requested shader/analysis panel;
6. read only the attached view's safe projection.

Bulk generic model reads are unsafe. InstructionMixModel advertises 15 source columns while its
attached views expose five; reading internal columns crashed the Viewer. Eager custom-role
enumeration also added shutdown instability without useful facts. These controls remain probe-only.

The 30-frame trace previously raced selection when only one of 88 metric tables existed and spawned
CrashReporter. The bridge now has minimum-count/stability barriers for metrics, source providers,
and asynchronous analysis models. Empty filtered models cannot be reported as complete, and a
timed-out background analysis is externally terminated instead of forcing unsafe Viewer shutdown.

## Hard boundaries

- The matching Viewer must open the trace. This differs from RazorProbe's official offline SDK.
- Standard Viewer does not expose full SASS opcode text; the product reports the Pro requirement and
  still returns SASS addresses and SASS-to-DXIL line association.
- HLSL/function hotspots require matching shader PDBs. Missing PDB is an input limitation, not an
  empty successful model.
- PC sampling is reliable at pass/marker range grain, not at one draw. Draw selection remains valid
  for API parameters and pipeline membership.
- GPU Trace contains performance/execution metadata, not Graphics Capture render-target or resource
  contents. It can localize suspicious work but cannot prove a pixel-level visual error.

## Architecture consequence

The generic probe is now an oracle, not the product API. Product operations expose only semantic
facts and hide all model/widget/activation details behind a concrete NsightViewer2026_2 adapter.
Atom coverage is completed before wrappers and Agent workflows are added.
