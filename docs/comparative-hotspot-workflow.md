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

    trace.info
        |
    trace.events / trace.analysis
        |
    resolve-event -> exact target and baseline EventKeys
        |
    inspect-pass(target) + inspect-pass(baseline)
        |
    compare-ranges -> exact target-minus-baseline facts
        |
    trace.shader-source for one returned ShaderKey, only when needed
        |
    counter catalog/export only for a named missing fact

`inspect-pass` closes range metrics, range shaders, and range instruction mix. Metrics and
instructions are complete. Shader output is a deterministic sample-count Top-N with total and
returned sample counts, coverage percentage, and explicit truncation. Samples remain PC-sampling
attribution candidates, not precise shader GPU time.

`compare-ranges` joins metric identity by exact table/row/column name and occurrence. Each side
retains its own source ordinal. Missing on one side remains target-only or baseline-only; it never
becomes numeric zero. Metric deltas are paged and every table has matched/changed/comparable counts.

## CLI shape

Resolve an exact occurrence, preferably within an already resolved frame or parent range:

```powershell
nsight-analyzer resolve-event trace.ngfx-gputrace `
  --event-name GBufferPass --event-occurrence 0 `
  --within-event-ordinal 1234 --compact
```

When `--within-event-ordinal` is present, the wrapper starts the paged Event List scan at that
exact ordinal, verifies the returned scope root, reconstructs the selected ancestor chain from
that root downward, and stops at the first event outside its subtree. The scope root itself is not
an occurrence candidate; matching remains strictly within the requested ancestor.

Inspect the returned preorder ordinal:

```powershell
nsight-analyzer inspect-pass trace.ngfx-gputrace `
  --event-ordinal 1773 --top-shaders 10 --compact
```

Compare two ranges in one trace:

```powershell
nsight-analyzer compare-ranges trace.ngfx-gputrace `
  --event-ordinal 1773 --baseline-event-ordinal 3901 `
  --cursor 0 --limit 100 --top-shaders 10 --compact
```

For before/after traces, add `--baseline-trace before.ngfx-gputrace`. Use repeated exact `--table`
filters after the table summaries identify the evidence families that need full inspection.

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

This is enough evidence to keep event search as wrapper policy rather than adding an event-search
atom. Repeated full model closure is now the larger mechanical cost. Further product work should be
driven by another real investigation: either a true same-pass duration regression or a repeated
frame comparison that proves which exact facts a `compare-frames` wrapper would remove from the
Agent's mechanical workload.
