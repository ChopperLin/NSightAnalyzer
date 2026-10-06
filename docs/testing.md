# Verification strategy

## Test layers

1. Contract tests run without Nsight or a GPU against sanitized bridge JSON.
2. Projection tests close semantic pages against fixed raw probe output.
3. Optional Viewer integration opens a caller-supplied real trace and validates exact build,
   selection, model stability, output freshness, and CrashReporter lifecycle.
4. Oracle closure compares product atom output to probe 0.44 on the same trace and selector.

Generic model discovery is never part of product verification.

## Contract test project

`tests/NsightAnalyzer.Tests` runs layers 1 and 2 with `dotnet test`. It needs no GPU, no Nsight
installation, and no real report.

Fixtures under `tests/NsightAnalyzer.Tests/Fixtures` are real bridge output passed through
`tools/fixtures/sanitize-bridge-output.py`. That script renames only caller-authored
instrumentation names (dotted, underscored, or interior-CamelCase tokens such as
`GBufferPass` or `MeshSkinning.SkinOnGPU`), pointers, and machine paths. Viewer vocabulary is
preserved exactly, because the projection asserts on it: column headers, role names, metric
table/row names, units, and D3D12 call text. Every number, row/column position, occurrence,
source ordinal, and availability state is preserved verbatim, so a fixture still closes the
same structural oracles as the report it came from — 88 tables, 409 rows, 801 metric values,
56 instruction categories, and a 100-of-4,951 event page.

Shader hashes are semantic identities: the sanitizer preserves their equality/inequality relations
and never replaces every hexadecimal hash with a generic pointer. The source-hotspot fixture uses
`sanitize-source-hotspots.py` to remove source text while preserving row/sample/view identity.

Regenerating a fixture is deliberate: run the script against a run directory under `.local/runs`
and confirm the oracle counts above still hold.

## Fixed real-trace oracles

### Generated validation trace

- 18 register, SysL2, and VRAM values across two markers exactly equal the official
  GPUTRACE_REGIMES.xls export.

### Real single-frame trace

- `trace.outline` visits 4,951 events and returns 493 range-grain rows in one call, keeping true
  preorder ordinals (`GBufferPass` stays 1773) and labelling each row container or marker;
- GBuffer and Deferred ranges return different register/L2/VRAM values;
- draw event 1661 returns InstanceCount 94206 and StartInstanceLocation 262176;
- shader c709 returns 3,592 samples, 24 warps, 30 registers, 14 live registers, 544 static
  instructions, and 1,501 Long Scoreboard samples;
- its source projection contains 480 DXIL rows and 242 SASS address rows.
- the frozen range Instruction Mix oracle contains 56 categories; pipe `FMA`, family `FP32 Math`
  has 61,179 instructions and 10,343 samples for the tested GBuffer range. A fresh live Viewer can
  instead report `trace.range_instruction_mix_not_loaded`; that state must never become empty rows;
- built-in export contains 226 ranges and 646 counters, removes its generated trace copy, and
  reports GBuffer PS register allocation 62.7392% versus Warp Metrics 62.739219%.
- `find-events ClearRenderTargetView` finds 52 exact-key candidates; scope ordinal 1680 contains
  seven strict descendants at ordinals 1772 and 1774-1779.
- the Vulkan descriptor fixture contains bind, descriptor-set and barrier commands, but exact draw
  selection creates no binding/resource model; marker 9 and draw 13 retain the same four Shader
  Pipelines roots.

### Real 30-frame trace

- event total 306,576;
- offset 306570, limit 10 returns six nodes, hasMore false, ending in Present event 276761;
- frame 0 and frame 29 GBuffer each return 88 tables and 409 rows;
- 258 of 376 unique logical metrics change;
- frame-0 GBuffer returns 1,020 shader hashes, 133 sampled, and 27,432 shader samples.
- Trace Analysis returns 30 frames; frame 0 is 25.49 ms with top issues GPU Engine Activity 70.1%,
  VRAM Limited 10.1%, and L1TEX Long Scoreboard 9.2%.
- frame 13 and frame 29 align one-to-one with direct Present occurrences. Their Trace Analysis and
  consecutive-Present deltas are both 5.43 ms at Viewer display precision; the disjoint selected
  event decomposition is +5.19 ms before, +0.26 ms inside, and -0.02 ms after.

## Atom gates

Every collection test verifies total, returned, cursor, next cursor, truncation, and concatenated-page
closure. Duplicate names remain distinct. Unknown properties and invalid bounds fail.

Every scoped atom verifies the final observed EventKey equals the requested key. A stable metric
snapshot under the wrong selection is a failure.

The Viewer-process shader cache is tested as a semantic optimization, not call-order state. The
first exact request must produce a complete/stable/non-truncated model before admission. Repeating
that request must return the same projected value hash; alternating GBuffer (1773),
DeferredLighting (3901), then GBuffer again must use distinct keys and restore the original GBuffer
hash. Every hit also records equal target/current event paths. Instruction Mix, action,
timeout, error, and artifact-writing requests remain misses.

Probe 0.50 real timings on the single-frame fixture: a fresh-session GBuffer shader request took
16.07 s, including report open; already-open uncached GBuffer/Deferred requests were 9.18-10.17 s;
cache hits were 1.21-1.34 s. `inspect-pass` took 16.91 s from a fresh Viewer and 3.02 s on repeat.
The GBuffer and Deferred projected page hashes remained stable across G/D/G switching.

Probe 0.51 adds compact Agent entry points. Warm `find-ranges --grain marker --limit 20` took
2.10 s, visited all 4,951 events once, and returned 10.4 KB including shared ancestor context. A
name-filtered three-candidate result was 3.6 KB. `trace.range-metric-catalog` returned all 88 tables
in 1.88 s and 22.4 KB; every raw metric export was header-only and carried no `nodes`. The first
singular shader profile took 9.23 s to establish the shader snapshot in an already-open Viewer, then
1.25 s / 3.9 KB from cache while re-verifying the exact EventKey.

The first `find-events ClearRenderTargetView --limit 20` request took 8.60 s, traversed 4,951 events
once, and returned 10.7 KB. The next scoped request took 2.08 s and returned all seven matches in
3.7 KB. Both used one atom call; the second request paid no report-open warmup.

Range metrics, range shaders, and range instruction mix additionally verify scope grain. The Viewer
reports an event range as a single command index or an inclusive span; only a span is a pass/marker
range. A single-command scope returns `unsupported` / `trace.unsupported_draw_scope` (SCP-002)
rather than a plausible value, because its PC-sampling denominator expands beyond the selected
command. Real-trace closure: single-frame ordinal 1774 (`ClearRenderTargetView`) is refused by all
three atoms with exit code 4, while ordinal 1773 (`GBufferPass`) still returns 88 tables, 409 rows,
801 metric values, 56 instruction categories, and 676 shader identities.

Every real run verifies:

- exact Viewer product version/build and bridge presence;
- fresh artifact with matching requestId and trace reportId;
- expected schema and complete status;
- bounded elapsed time and output;
- Viewer exit or controlled session state;
- no additional/replacement CrashReporter beyond the live session's startup-recorded child, and no
  surviving child after session close.

## Wrapper gates

- `find-events` traverses the Event List once, pages the matching subsequence in strict preorder,
  preserves true EventKeys, and verifies an optional exact ancestor before applying scope;
- `find-ranges` traverses the Event List once, returns a bounded duration-ordered page, preserves
  true EventKeys, and verifies an optional exact ancestor before applying strict-descendant scope;
- `resolve-event` scans stable preorder pages, requires an explicit occurrence, and never chooses
  an ambiguous same-name range implicitly;
- `inspect-pass` closes every available source page, verifies every returned scope against the
  exact event, reports shader truncation plus sample coverage, and marks range Instruction Mix as
  explicitly not requested; the independent atom owns that stateful fact family;
- `compare-ranges` joins metrics by table/row/column name plus occurrence, retains source ordinals
  on both sides, preserves target-only/baseline-only, and computes deltas only for comparable values;
- `compare-frame-timing` requires explicit target/baseline frame EventKeys, frame indexes, Trace
  Analysis seed, and Present queue; it verifies a contiguous one-to-one frame/Present sequence and
  rejects unaligned timing rather than guessing context;
- frame timing exposes the preceding-Present-to-event-start, selected-event, event-end-to-Present,
  and consecutive-Present intervals as Viewer-display arithmetic. Bottleneck labels remain absent;
- metric deltas have stable cursor/limit paging and per-table completeness summaries;
- the real single-frame Dispatch fixture whose Viewer model reports a dimension above the D3D12
  semantic maximum returns unavailable instead of a successful workload fact;
- duration deltas identify Viewer display precision as their basis;
- different event names or depths produce objective warnings, not a diagnosis.

The single-frame wrapper closure resolves GBuffer to preorder ordinal 1773 and DeferredLighting to
3901. Their mechanical comparison closes 801 matched metrics and a
56-versus-52 instruction surface (57 joined identities). The real 676-row shader inventory contains
repeated stage/hash/name/pipeline tuples: cross-range shader comparison now explicitly returns
`wrapper.shader_comparison_ambiguous`, because row occurrence alone cannot prove correspondence.
This sibling comparison validates the
wrapper contract only; it is not accepted as causal evidence.

## Safety

Real traces, shader source, raw model output, Viewer logs, and generated counter exports remain in
.local/. Verification only removes run directories it created and resolved under that root. It never
deletes or overwrites the caller's report.

## CLI v2 efficiency and identity checks

`AgentEfficiencyTests` closes brief output against detailed fixture facts, verifies complete metric
pagination and shader coverage, bounds the short catalog and default inspection response, checks
section-independent comparisons and actionable overflow errors, byte-fits variable-width shader
pages in both compact and pretty JSON while retaining a stable continuation cursor, and round-trips
stage/hash/occurrence into shader-source selection. A sorted proxy may have a different row path;
semantic identity must still match. Duplicate source identities that the pinned bridge cannot
distinguish are unavailable.

The published skill includes `references/analysis.md` and `references/recovery.md`. Package smoke
checks use schema 2 and default concise output. Interactive dogfood records default (no formatting
flags) discovery, pass inspection, metric-only comparison, shader profile/source round-trip and session
close: elapsed time, response bytes, selected identities, counts and no retained CrashReporter.

## CLI v2.1 accuracy and efficiency

`AccuracyRegressionTests` checks missing versus empty versus zero samples, explicit table coverage,
shader join ambiguity and row reordering, and numeric source/resolution changes without a value delta.
`MetricSnapshotTests` covers trace path/snapshot, session/PID/process instance, adapter identity,
content integrity, and header-catalog closure. `BatchTimingTests` checks explicit pair validation,
current-page execution, exact context separation, caller sample groups, and unavailable timing exclusion.
`FindMetricsTests` and `SourceHotspotsTests` close metric search and source hotspot ordering/coverage to fixtures.
`CliUsabilityTests` checks machine-readable option constraints, duplicate arguments, read-only doctor,
version identity and absolute workspace routing.

On 2026-10-04, a single-frame real Viewer run under an isolated workspace verified:

- timing-only comparison: two event calls, no metrics/shaders, 2,573 stdout bytes;
- first and subsequent metric pages: 801 total, 20 returned, 6.7–6.9 KB stdout; warm calls took
  1.79–2.02 s, including fresh header-catalog scope validation; the raw bridge export fell from
  1,902,604 bytes to 511,917 bytes (73% less) on a snapshot hit;
- GBuffer/Deferred/GBuffer switching retained exact keys, and mixed existing/missing table requests failed;
- named metric search followed by source-ordinal retrieval returned the same exact cell (1,550 bytes);
- c709 source hotspots: Top 20 captured 2,416 of 3,592 DXIL samples (67.260579%), 12,217 stdout bytes,
  in 24.86 s including source readiness; the full underlying view has 480 rows;
- different client working directories with the same workspace reused the same Viewer PID;
- session close removed the owned Viewer and CrashReporter; original report SHA-256 was unchanged.

Raw evidence is local at `.local/verification/agent-v21-20261004-073808/`. These are observed costs
on one report, not general latency guarantees. Metric snapshots primarily reduce repeated body export;
scope validation still costs a Viewer request. Source actions are not cached.
Shell smoke checks cover PowerShell, cmd and Python subprocess with Unicode/spaced paths and one JSON
result. They do not constitute end-to-end evaluations of external Agent models.

After building Release, repeat this workflow against the frozen single-frame report with:

```powershell
pwsh -NoProfile -File tools/verification/verify-agent-workflow.ps1 -SingleFrameTrace <report.ngfx-gputrace>
```

The script writes evidence under an isolated `.local/verification/agent-v21-*` workspace, checks both
batch pages, and closes/reopens Viewer to prove a new session cannot reuse a previous instance's metric
snapshot. It verifies the caller's original SHA-256 and cleans up the owned Viewer/CrashReporter in `finally`.
Use `-CacheOnly` for focused cache/session checks, and `-CliPath <exe>` to test a packaged executable.
Each cache receipt must be newly created by that invocation and close against its adjacent bridge output,
selected scope, and completed session request. Cleanup checks include additional/replacement CrashReporter
children using exact parent/process identity.

The full regression at `.local/verification/agent-v21-20261004-074802/` confirmed two timing pages each
perform exactly two event calls, ambiguous shader joins fail explicitly, and Viewer reopen invalidates old
metric snapshots. The final packaged CLI passed the strengthened cache/session checks at
`.local/verification/agent-v21-20261004-075701-bd28fbf9/`; its verified package content fingerprint is
`0e8e41f51f034fe7c40e1e08cd9684d2c7c7b65e03a96cbfa1741d640f6bb482`.
