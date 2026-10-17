# Pass regression across captures and source revisions

Use for two or more GPU Traces of a named pass, including improvements, regressions,
and questions such as whether an indirect-dispatch change caused a slowdown.
Apply [analysis.md](analysis.md) for shared metric/sampling rules and
[cli.md](cli.md) for paging and command contracts. Preparation and session cleanup
remain in SKILL.md. No new capture, replay, code modification, or benchmark is
implied by this analysis workflow.

## Fast path

Resolve comparable scopes -> establish timing distributions -> locate the growing
component -> check workload changes -> retrieve discriminating metrics/shaders ->
inspect the relevant source revisions -> state the strongest supported conclusion.

Start with adjacent builds and retain the earliest comparable baseline. This is
linear in the number of captures; do not compare every pair or dump every metric.
If an intermediate capture changes configuration, analyze the intervals separately.
An observed chronological trend is still useful when a controlled comparison is
unavailable, but it does not isolate a feature's cost.

## 1. Record input identity and resolve the pass once

Keep a small local manifest under the task workspace's `.local/`: trace path and
returned identity, chronological label, build/revision, pass selectors, relevant
settings, and the evidence source for each. Preserve unknown fields. Reuse identity
and decoder provenance returned by successful operations; call `trace.info` only
for a missing identity fact. The reader's version is not the capture producer's
version. Filename dates/revisions and current source HEAD are not verified build
metadata; record them as labels or assumptions until corroborated.

Use `find-ranges --name-contains <pass>` to collect candidates. It sorts by duration,
not chronology: exhaust the needed pages and recover frame/queue/parent context
before constructing a series. Do not select the first row or equate occurrence
number with frame number. Distinguish repeated cameras, reflection views and queues.
Record each exact EventKey with its trace; keys cannot be transferred across traces.
For renamed/split passes, document the mapping or leave it unresolved.

Check comparability using available capture/build evidence: device/driver, clocks,
camera/scene, resolution, quality, sampling/denoising mode, and capture settings.
Unknown configuration does not block measurement; it limits attribution. Ask only
for information that prevents choosing the intended pass or comparison, while
continuing independent evidence collection.

## 2. Establish the trend with inexpensive timing facts

Reuse durations from resolved range/event rows. Use `inspect-pass --sections timing`
only for missing timings. For a short multi-frame capture, include all valid
occurrences of the chosen context. For a large series, declare a bounded sampling
rule and coverage; do not take only the duration-sorted first page. Preserve outliers
and report their component separately rather than silently discarding them.

For each capture report sample count, median and a spread (range or quantiles), with
units and source precision. Compare adjacent medians and the baseline. Do not claim
more precision than the underlying display values; an even-sized median can create
an extra decimal without adding measurement precision. Adjacent frames within one
capture are not independent repeat runs.

Cross-trace frame indexes are not automatically paired experiments. Compare the
per-capture distributions unless a frame correspondence is supported. When genuine
explicit pairs exist, reuse `compare-timings`; a pair file holds at most 32 pairs,
and summaries cover only its requested page. See cli.md before grouping samples.
Compute larger-series statistics from the underlying observations, never from an
average of page medians or an unweighted average of percentages.

## 3. Decompose the change before collecting expensive evidence

Inspect the selected pass's API events and nested markers. Locate which ray
dispatch, compute kernel, draw group or interval accounts for the change, then
extend that timing breakdown across the chosen samples. Match components using
captured markers/pipeline evidence and the corresponding source dispatch chain;
command position alone is not a durable cross-build identity.

**Flat-marker trap:** a marker can have `childCount = 0` while its `eventRange`
spans many API commands stored as siblings in the Event List. `--within-event-ordinal`
uses tree ancestry and can therefore find no descendants of that marker. This is
not an empty GPU workload. Use a real enclosing frame/queue ancestor for discovery,
or page `trace.events --cursor <returned-marker-ordinal>` locally. Verify the start
key, frame/queue context and numeric event-range containment; stop after proving
the marker span has been passed. Do not use a fixed page size or a guessed number
of commands as proof of completeness. If ordering/context cannot establish the
boundary, report incomplete membership.

For disjoint serial components, sum their durations within each frame before
summarizing. Medians of components need not add to the parent median. Account for
overlap and rounding before interpreting parent-minus-components; this residual
alone is not measured API overhead. Keep AS builds or other work outside the pass
separate, even when their outputs affect the pass.

If growth is inside GPU work, investigate workload/shader execution first. If it
is in verified idle/gap intervals, follow timeline/queue evidence; do not start by
optimizing the shader with the highest stall percentage.

## 4. Check workload, then narrow the metric/shader investigation

Choose an occurrence near the median of each relevant distribution for detailed
inspection. Before generalizing a mechanism to the capture, check another temporal
location; expand only if they disagree or a distinct tail problem matters. Record
which frames supplied timings versus counters/samples. Never present two detailed
frames as metrics measured across all frames.

First inspect `trace.event-parameters` for the decisive draw/dispatch. Separate
direct dimensions, thread-group dimensions, CTA dimensions, target size, indirect
command count, and actual GPU-generated work count. `ExecuteIndirect.MaxCommandCount`
does not specify ray/thread count. An argument-buffer address/offset is not its
contents. Total launched CS+VTG threads are not the number of effective TraceRay
calls. For RT, inspect relevant AS build parameters when available; TLAS instance
count establishes a workload difference, not triangle count or traversal cost.

Then use `inspect-pass`, `compare-ranges` and focused `find-metrics` queries at the
exact marker scope. Discover unknown table/column meanings through the catalog or
`--detail`. Request only families that distinguish the remaining explanations:

| Observed change | Next evidence to retrieve together |
|---|---|
| Dispatch shape, resolution, compaction or sampling changed | Exact command parameters, launched work, captured/source setting evidence and component duration |
| RT stage mix changed | Raygen/hit/any-hit/miss and identifiable RT runtime samples, relevant AS inputs, then the dominant returned ShaderKey's source |
| Register/occupancy concern | Comparable shader static/live registers, CTA/shared memory, range register allocation, active warps and duration |
| Instruction supply concern | No Instructions counts/denominator, instruction-cache hit/miss traffic, issue activity and duration |
| Memory/dependency concern | Long Scoreboard/latency, cache and traffic facts, sampled load/consumer chain and duration |

Reuse the shared evidence rules for bandwidth/compute branches. A percentage or a
static register count alone cannot establish the limiter. Separate static code
size/instruction count from sampled or dynamic instruction work. Cache request
bytes are not shader binary size or automatically VRAM traffic.

Start with shader Top-N plus coverage. Extend it for a missing stage or broader
claim; full inventory is needed only when claiming absence/completeness. Static
facts for a few dominant shaders are usually sufficient initially. Do not turn
sample share into shader milliseconds, invocation counts or per-ray cost.

Use returned stage/hash/occurrence for `trace.shader-profile` or
`find-source-hotspots --view dxil`. Fetch full source only for the relevant hot
block. A changed hash or range-local pipeline identifier is not a cross-build
semantic mapping. Use source entry, variant/build evidence and available correlation
to justify a mapping; otherwise retain unmatched shaders. A comparison wrapper's
ambiguous shader join does not invalidate its separately obtainable timing/metrics.

For RT, separate identifiable application stages from driver/runtime traversal
before interpreting shares. Do not classify an unknown shader by its hex name.
Check membership before adding stage or instruction rows: correlated views and
dependency-attributed samples are not independent observations.

### When the question is "all stalls are No Instructions"

Identify the selected scope and denominator first: one shader, Top-N, application
shaders, or the pass including runtime/unattributed work. Recompute a broader share
only from a complete, compatible sample population, retaining counts and coverage.
Label whether Selected/Not Selected are included; these are scheduler states, not
stalls. Do not assume instruction-mix totals include RT runtime rows.

No Instructions concerns instruction fetching, including instruction-cache misses;
it does not establish missing CPU submission or missing PDB. Use the returned
metric description and corroborate with cache/issue/duration evidence. If its
share and cache behavior remain similar in the next interval, seek the newly
changed workload instead of reusing the previous interval's explanation.
For hardware terminology, see NVIDIA's [stall-state definitions](https://docs.nvidia.com/nsight-compute/ProfilingGuide/index.html#metrics-reference);
this is a terminology reference, not a request to run Nsight Compute or import its
CUDA thresholds into GPU Trace analysis.

At a source hotspot, a sampled consumer instruction may be waiting for an earlier
load. Trace the local dependency before blaming a compare/arithmetic instruction.
Missing PDB or unavailable SASS text limits source attribution; use available DXIL
and say what cannot be mapped rather than repeatedly requesting the same evidence.

## 5. Connect the measured change to source history

Read history only after timing/workload has narrowed the component. Use repository
search conventions and read-only Git/SVN history/diffs for the relevant build
interval and dispatch, shader, configuration or RTAS producer paths. Inspect the
code at those revisions, not only current HEAD or commit messages. Check the
material/variant and shared-include callers when the suspected change reaches them.

Keep three statements separate: what the capture executed, what a source revision
changed, and whether that build/setting is verified in the capture. A default change
does not prove a runtime override was absent. If build identity is only a filename
label, make the source link conditional; it cannot establish the culprit commit.
Unchanged shader text does not exclude behavior changed by activation, inputs,
defines, compiler or runtime selection.

For each leading explanation close as much of this chain as the evidence allows:

    revision/configuration change -> executed workload or code path
        -> relevant counters/stages/instructions -> component duration

Check a competing explanation and any contradictory interval. For example, a
direct-to-indirect change that also changes resolution, VRS or compacted pixel order
does not isolate API overhead. An API-only comparison needs the same work list,
actual count, shader, RTAS and relevant settings. Likewise, new alpha-test any-hit
work plus increased vegetation shading can support a content-cost explanation,
but does not identify a particular material without matching evidence.

## 6. Deliver a bounded conclusion and stop

Keep the local manifest, original JSON envelopes/source diffs, and a concise
comparison/report. Link decisive values to trace identity, EventKey/ShaderKey,
metric identity and evidence file. A useful summary table is:

    capture/build evidence | context/sample count | pass median/spread
        | growing component | workload change | decisive metric/shader evidence

State separately what is measured, the best-supported explanation, and what remains
unresolved. Name a culprit revision only when the causal chain supports it. Do not
force every increase into one cause or sum percentages from different scopes.

Stop when the decisive comparison supports the conclusion, when another table
cannot distinguish the remaining candidates, or when required facts are unavailable.
If causality remains open, specify the smallest useful same-build/configuration A/B
and the quantities that should move together; do not launch that experiment as part
of reading existing traces. Missing optional source/mix data must not erase valid
timing/workload results. Close owned sessions with the original workspace/viewer.

## Capability boundary

The existing atoms cover this workflow's retrieved facts. N-trace scheduling,
flat-marker membership, paging and statistical aggregation are composition work;
promote repeated mechanical work to a wrapper only when validated by real inputs.
Do not add a new atom merely to encode a diagnosis.

On the motivating RTR reports, `trace.event-parameters` exposed indirect buffer
handles/offsets and command limits, but not the GPU-written dispatch dimensions/count.
Record that as unavailable from the current interface. A future decoded-report
fixture exposing that fact could justify extending event parameters; reading raw
GPU buffers or parsing the proprietary container is not an alternative here.
Build/settings manifests and shader-to-material/variant correlation may likewise
require external build/symbol evidence. These gaps limit exact attribution and
must not be filled with target dimensions, defaults or guessed shader identities.
