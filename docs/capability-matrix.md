# GPU Trace capability matrix

## Status

- verified: product atom exists and closes against an offline fixture and/or the named real trace.
- implemented: product atom exists but its full acceptance variants have not closed.
- probed: probe 0.44 recovered and validated the fact; product atom is not complete.
- unavailable: the selected report/build/input cannot provide the fact.
- out of scope: not a fact of an existing GPU Trace report.

The current 95 percent estimate describes the proven ordinary-analysis data surface, not product
implementation completeness.

The public catalog additionally labels three deterministic wrapper operations: `resolve-event`,
`inspect-pass`, and `compare-ranges`. They compose the verified fact families below and do not add
new facts or bottleneck judgments.

| ID | Semantic fact family | Probe evidence | Product status | Acceptance |
|---|---|---|---|---|
| ENV-001 | capabilities/decoder preflight | exact 2026.2 build and plugin checks | verified | refuse mismatched Viewer/bridge |
| TRC-001 | trace identity | local snapshots for both real reports | verified | non-empty extension, stable identity, optional SHA-256 |
| NAV-001 | event page | 306,576-node report and final six-node page | verified | exact page closure, stable EventKey |
| NAV-002 | event parameters | DrawIndexedInstanced typed values; out-of-domain Dispatch fixture | verified | exact scope and typed leaf projection; invalid Viewer-decoded D3D12 values return unavailable |
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
| ANA-001 | range instruction mix | 56-category GBuffer and 52-category Deferred | verified | exact range scope, samples/instructions/stalls |
| ANA-002 | Trace Analysis | 30 per-frame models with top issues | verified | explicit pass seed, target-model barrier, paging |
| CNT-001 | counter catalog/values | 226 ranges by 646 counters | verified | duplicate occurrences and safe trace-copy cleanup |
| SCP-001 | pass/marker PC sampling | stable on tested ranges | verified | only proven range grains |
| SCP-002 | single-draw PC sampling | sample denominator expanded unexpectedly | unavailable | explicit unsupported-scope |
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
