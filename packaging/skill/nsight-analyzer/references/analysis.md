# Evidence rules for GPU Trace analysis

Resolve scope before explaining a number. Repeated marker names require frame,
parent and occurrence context. Compare ranges at the same granularity; a parent
marker includes its descendants, so do not sum or rank them as independent costs.
Multi-queue overlap also prevents treating summed durations as frame wall time.

For a regression, prefer the same pass in a normal frame or a before-change trace.
Use exact target-minus-baseline values, units, join states and display precision.
Target-only and baseline-only facts are missing matches, not zero-valued changes.
Honor numeric state, value source and resolution. A displayed rounded number or
bound such as `< 0.01` does not justify a more precise delta; `numericComparableCount`
does not mean every matched metric was numerically comparable.
A changed hash or range-local pipeline identity is not proof that a shader is new.
State unresolved matching rather than inventing a correspondence.

Timing-batch summaries cover only the executed page (`statisticsBasis: currentPage`).
Inspect the summary's `groupingBasis` and context: matching marker text alone does
not establish repeated comparable work. The default grouping retains exact
parent paths, object and thread on both sides. An explicit `sampleGroup` permits
cross-frame samples as a caller assertion; verify compatible settings and work
before using it. Event names and depths must still agree. A caller label or group
is not trace evidence, and page medians cannot be averaged into a global median.

Start with timing when durations answer the question. For deeper analysis, retrieve focused
metrics; add `shaders` only for attribution, and `instruction-mix` only
for its dynamic range-level instruction and stall facts. Request exact metric tables
from the catalog for a concrete evidence gap. Fetch source for a returned ShaderKey
only when line-level evidence is needed. Counter export is for a named missing fact.

| Hypothesis | Evidence to seek together |
|---|---|
| Occupancy constraint | Range allocation/active-warps changes and shader static/live register facts |
| Memory latency | Scoreboard samples, cache/traffic/latency changes, and range duration |
| Bandwidth pressure | Throughput, transferred bytes, workload and duration |
| Compute/workload growth | Instruction families, dispatch/draw parameters and relevant shader attribution |
| GPU idle or pacing | Activity and exact timeline intervals; GPU Trace alone may not identify a CPU/synchronization cause |

No single percentage establishes a cause. Trace Analysis findings are Viewer
observations, not causal proof. PC sample counts provide attribution, not precise
shader GPU time; a sampling share is not a shader duration percentage. Samples from
different scopes require compatible sampling context before interpreting deltas.
Keep sampled/total counts and Top-N coverage in the reasoning.

Use draw/dispatch scopes for event parameters. Use pass/marker scopes for metrics
and PC sampling; the pinned adapter rejects unreliable single-command sampling.
Range register-file allocation and shader static register counts are different facts.

Missing PDB HLSL and unavailable Standard-edition SASS text are evidence limits.
Ambiguous source rows with identical stage/hash/name/pipeline are unavailable; do not
merge occurrences or attribute another row's source. Available DXIL or SASS addresses do not imply that source/opcode text is available.

Keep the user answer short: observation, decisive target/baseline values, hypothesis
with its support, and the next discriminating check only if needed. Without a
comparable control, label causal claims as hypotheses and state the missing control.

When multiple settings changed, record the capture's measured outcome separately
from configuration evidence (build manifest, settings dump, code change or an
explicit user statement, naming which source supports each setting). A feature
name, changed shader hash or performance pattern does not prove a feature flag.
Compare one changed setting at a time where captures permit. For two interacting
settings, an available four-way control (both off, A only, B only, both on) can
distinguish the individual effects from their interaction; do not require new
captures when the question can already be answered from existing evidence.

For example, slower performance after enabling SER does not isolate SER's effect
if thread resort was also disabled. Show the observed duration/workload changes,
the separately supported settings change, and which control is missing before
attributing the regression to one setting. The CLI does not infer those settings.

Stop when the decisive comparison is supported, when additional tables would not
distinguish the remaining hypotheses, or when the report lacks the required fact.
State that gap once and identify the smallest useful next check. Do not exhaust
every page or repeatedly ask for unavailable PDB/SASS evidence.
