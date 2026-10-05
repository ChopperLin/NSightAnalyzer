# Comparative hotspot workflow

## Goal

The primary workflow answers four questions in order:

1. which same-grain frame/pass range is slower;
2. which objective metric, instruction, stall, or workload facts changed;
3. which returned ShaderKey owns the strongest attribution evidence;
4. whether the same evidence moves in a normal frame or pre-change trace.

A single trace may support a hypothesis. A slow/normal frame pair from the same pass, or a
before/after trace pair, is the preferred validation input. Trace Analysis may prioritize a range,
but it is not causal proof.

## Evidence path

    find-ranges / find-events (only when exact keys are not known)
        |
    exact target and baseline EventKeys at comparable granularity
        |
    compare-ranges (metrics; add shaders or instruction-mix for a named evidence gap)
        |
    trace.shader-profile / trace.shader-source for one returned ShaderKey if needed
        |
    counter catalog/export only for a named missing fact

Use `trace.outline` only for missing hierarchy context. Use `compare-frame-timing` only for
explicit frame/Present interval questions, with the required seed and queue identities.
`trace.info` is for decoder/trace identity checks, not a mandatory warm-up before each task.

`inspect-pass` reads metrics and shaders, returning a bounded metric page and a sample-count
Top-N with total/returned sample counts, coverage percentage and explicit truncation. CLI v2
defaults to 20 metrics and five shaders. `--detail` includes descriptions and instruction vectors
without disabling paging. Samples remain PC-sampling attribution, not precise shader GPU time.

`compare-ranges` reads metrics by default. Use `--sections metrics,shaders` for shader attribution,
or explicitly request `instruction-mix` for range instruction/stall deltas. Every requested family
must succeed. An unavailable Instruction Mix does not prevent a metric-only comparison.

`compare-ranges` joins metric identity by exact table/row/column name and occurrence. Each side
retains its own source ordinal. Missing on one side remains target-only or baseline-only; it never
becomes numeric zero. Metric deltas are paged and every table has matched/changed/comparable counts.

`compare-frame-timing` is deliberately narrower than a general frame matcher. The caller supplies
both same-grain frame EventKeys and frame indexes, one real pass/marker EventKey as the Trace
Analysis seed, and the exact queue EventKey containing Present commands. The wrapper verifies the
zero-based Trace Analysis frame sequence against direct Present occurrences, then returns only the
consecutive-Present interval, the intervals before/inside/after the selected event, display
precision bounds, and target-minus-baseline arithmetic. It does not name a CPU, synchronization,
or pacing cause.

## CLI shape

Resolve an exact occurrence, preferably within an already resolved frame or parent range:

```powershell
nsight-analyzer resolve-event trace.ngfx-gputrace `
  --event-name GBufferPass --event-occurrence 0 `
  --within-event-ordinal 1234
```

When `--within-event-ordinal` is present, the wrapper starts the paged Event List scan at that
exact ordinal, verifies the returned scope root, reconstructs the selected ancestor chain from
that root downward, and stops at the first event outside its subtree. The scope root itself is not
an occurrence candidate; matching remains strictly within the requested ancestor.

Inspect the returned preorder ordinal:

```powershell
nsight-analyzer inspect-pass trace.ngfx-gputrace `
  --event-ordinal 1773 --top-shaders 10
```

Compare two ranges in one trace:

```powershell
nsight-analyzer compare-ranges trace.ngfx-gputrace `
  --event-ordinal 1773 --baseline-event-ordinal 3901 `
  --sections metrics,shaders --cursor 0 --limit 20 --top-shaders 5
```

For before/after traces, add `--baseline-trace before.ngfx-gputrace`. Use repeated exact `--table`
filters after the table summaries identify the evidence families that need full inspection.

Decompose two explicit frames from one trace:

```powershell
nsight-analyzer compare-frame-timing trace.ngfx-gputrace `
  --event-ordinal 131987 --target-frame-index 13 `
  --baseline-event-ordinal 295573 --baseline-frame-index 29 `
  --analysis-seed-event-ordinal 136633 `
  --present-queue-event-ordinal 306395
```

Frame index zero is intentionally unavailable in this first contract because there is no preceding
Present event in the report with which to form the same interval basis.

## Skill boundary

Wrappers do not label a bottleneck. The Skill may weigh several correlated changes into candidate
branches:

- occupancy: allocation/active-warp changes plus shader register facts;
- memory latency: scoreboard samples plus cache, traffic, and latency facts;
- bandwidth: throughput and byte traffic moving with duration;
- compute: ALU pipe and instruction-family growth tied to returned shaders;
- workload: draw/instance/dispatch/primitive parameters and instruction growth;
- GPU idle: low activity or timeline gaps, with an explicit statement that GPU Trace may not prove
  the CPU or synchronization cause.

Confidence is lower without a same-pass control. No single percentage is sufficient by itself.

For timing questions, start with `compare-ranges --sections timing`. Use `compare-timings` for
explicitly paired samples; its summaries cover only the current page and keep exact contexts
separate unless the caller declares a `sampleGroup`. Use `find-metrics` for a specific metric and
`find-source-hotspots` for ranked sampled rows in one explicit source view.

Record configuration changes separately from trace observations. For example, an SER toggle coupled
with a thread-reordering toggle changes two variables; timing and counter deltas cannot by themselves
attribute the regression to SER. Seek build/configuration evidence and matched controls before
assigning causality. The wrappers expose no inferred feature-toggle state.

## Scope rules

- compare events under the same parent and at the same granularity; never sum or rank a parent
  marker together with its child draws;
- keep PC sampling on a real pass/marker range; the known single-draw scope remains unreliable;
- use draw/dispatch events primarily for `trace.event-parameters` workload facts;
- treat source attribution as evidence, not exact per-shader time;
- preserve unavailable HLSL/PDB, Standard-edition SASS, and other capability states.

## First fixture observations

On the real single-frame fixture and the pinned 2026.2 decoder:

- unbounded GBuffer resolution scanned 4,951 events through 10 atom calls in 43.8 seconds;
- the original bounded implementation still scanned the global prefix and needed four atom calls
  and 21.6 seconds; starting at exact ancestor ordinal 1680 resolves the same EventKey in one atom
  call, scans 101 in-scope events, and takes 10.9 seconds;
- GBuffer inspection used six atom calls in 65.7 seconds and emitted 332 KB compact JSON: 801
  metrics, 56 instruction rows, and shader Top 10 covering 66.66 percent of 39,508 samples;
- a mechanical GBuffer/Deferred comparison used 12 atom calls in about 131 seconds; after paging,
  its first 100 of 801 metric deltas emitted about 118 KB and all 801 semantic metric identities
  matched.

On the real 30-frame fixture, exact frame-13 scope ordinal 131987 closes GBufferPass ordinal 136633
through 20 atom calls, with 10,000 retrieved facts and 9,910 actually scanned in-scope events in
110.3 seconds. The previous global-prefix implementation exhausted its 240-second wrapper deadline
before reaching that ancestor. The same regression also verifies the fixture's Unicode file name
through the session heartbeat and requires graceful Viewer-session cleanup with no retained Viewer
or CrashReporter process.

The explicit frame-13/frame-29 timing comparison closes in four atom calls, retrieves 213 facts,
scans 183 event facts, and takes 106.5 seconds from a cold Viewer. Trace Analysis reports a 5.43 ms
target-minus-baseline delta; consecutive Present starts independently produce the same display
delta. The three disjoint display intervals around the selected sibling `ExecuteCommandLists`
events change by +5.19 ms, +0.26 ms, and -0.02 ms, which closes to +5.43 ms. These are timeline
facts only; the CPU/synchronization/pacing interpretation remains a Skill hypothesis.

This is enough evidence to keep event search and timeline alignment as wrapper policy rather than
adding either as an atom. Repeated full model closure is now the larger mechanical cost. A general
`compare-frames` pass matcher remains deferred until another real investigation proves which stable
identity and nesting rules it actually needs.
