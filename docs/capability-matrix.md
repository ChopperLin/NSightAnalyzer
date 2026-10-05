# GPU Trace capability matrix

## Status

- verified: product atom exists and closes against an offline fixture and/or the named real trace.
- implemented: product atom exists but its full acceptance variants have not closed.
- probed: probe 0.44 recovered and validated the fact; product atom is not complete.
- unavailable: the selected report/build/input cannot provide the fact.
- out of scope: not a fact of an existing GPU Trace report.

The public catalog additionally labels nine deterministic wrapper operations: `find-events`,
`find-ranges`, `resolve-event`, `inspect-pass`, `compare-ranges`, `compare-frame-timing`,
`find-metrics`, `find-source-hotspots`, and `compare-timings`. They
compose the verified fact families below and do not add new facts or bottleneck judgments.
`describe`, `doctor`, and `version` provide operation contracts and local environment/package identity.

| ID | Semantic fact family | Probe evidence | Product status | Acceptance |
|---|---|---|---|---|
| ENV-001 | capabilities/decoder preflight | exact 2026.2 build and plugin checks | verified | refuse mismatched Viewer/bridge |
| TRC-001 | trace identity | local snapshots for both real reports | verified | non-empty extension, stable identity, optional SHA-256 |
| NAV-000 | range-grain outline | 493 of 4,951 single-frame events are ranges | verified | bridge-filtered skeleton, true preorder ordinals, grain label |
| NAV-001 | event page | 306,576-node report and final six-node page | verified | exact page closure, stable EventKey |
| NAV-002 | event parameters | DrawIndexedInstanced typed values; out-of-domain Dispatch fixture | verified | exact scope and typed leaf projection; invalid Viewer-decoded D3D12 values return unavailable |
| NAV-003 | bounded event candidate search | 52 single-frame ClearRenderTargetView matches; seven under exact scope 1680 | verified | one traversal, true EventKeys, preorder paging, exact optional ancestor |
| MET-001 | range metric catalog | 88 tables, 409 rows | verified | exact tables/headers/descriptions/occurrences |
| MET-002 | range metric values | generated export equality and two real reports | verified | long-form values, precise Tooltip, paging |
| MET-003 | cross-frame refresh | frame 0/29 GBuffer values differ | verified | distinct EventKeys, no stale selection |
| SHD-001 | range shader inventory | 1,020 hashes, 133 sampled | verified | paged rows and stable ShaderKey |
| SHD-002 | shader profile | c709 fixed oracle values | verified | static/dynamic/stall/instruction projection |
| SHD-003 | shader instruction mix | row-specific vector verified | verified | distinguish shader mix from range mix |
| SRC-001 | DXIL/source rows | 480 c709 rows, 3,592 samples | verified | per-line samples/stalls/live registers |
| SRC-002 | SASS address correlation | 485 rows/242 addresses | verified | address-to-DXIL only in Standard |
| SRC-003 | HLSL/function rows | empty with exact missing PDB diagnostic | unavailable | available only when matching PDB is loaded |
| SRC-004 | full SASS opcodes | Standard product declares Pro requirement | unavailable | no license bypass |
| ANA-001 | range instruction mix | frozen oracle has 56-category GBuffer and 52-category Deferred; cold Viewer model is present but empty | conditional / not-loaded | exact range scope when available; otherwise explicit `trace.range_instruction_mix_not_loaded`, never successful empty data |
| ANA-002 | Trace Analysis | 30 per-frame models with top issues | verified | explicit pass seed, target-model barrier, paging |
| CNT-001 | counter catalog/values | 226 ranges by 646 counters | verified | duplicate occurrences and safe trace-copy cleanup |
| SCP-001 | pass/marker PC sampling | stable on tested ranges | verified | only proven range grains |
| SCP-002 | single-draw PC sampling | sample denominator expanded unexpectedly | unavailable | range/shader/instruction atoms refuse a single-command scope with `trace.unsupported_draw_scope` |
| EXE-001 | full event execution state | 380-model selection audit; draw and range expose the same 506 pipeline roots | unavailable | no exact topology, viewport, target, binding or resource-state model in this report/build |
| EXE-002 | binding/resource metadata and usage | Vulkan descriptor fixture has bind/barrier commands but no decoded model; related actions disabled | unavailable | pointer-like command handles are not stable resource identities |
| RES-001 | resource contents/pixel history | absent from GPU Trace domain | out of scope | requires Graphics Capture |
| PRF-001 | Nsight Perf SDK live metrics | different producer and workflow | out of scope | user-approved future scope only |

## Atom admission

A probed row becomes a verified atom only after:

1. semantic request/result contracts exist;
2. exact trace/scope identity and decoder provenance are returned;
3. selection and stability are validated internally;
4. paging closes against the raw oracle;
5. unavailable states are classified;
6. product JSON contains no Qt implementation identifiers;
7. real Viewer integration creates no CrashReporter beyond the session's startup-recorded guardian.
