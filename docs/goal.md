# GPU Trace atom-platform goal

## Objective

Given an existing .ngfx-gputrace, let an Agent retrieve the performance facts needed for ordinary
frame/pass/shader bottleneck analysis without screenshots or manual GUI traversal.

The first supported decoder is exactly:

- Nsight Graphics Viewer 2026.2.0.0
- build 37991608, public-release
- Qt 6.8.1, MSVC 2022 x64
- SolidProbe bridge schema `probe-0.46` (`probe-0.44` remains the frozen semantic oracle)

Other Viewer builds are unavailable until separately re-probed and accepted.

## P0 atom outcome

The atom layer is complete when callers can retrieve:

1. trace/decoder identity and capability availability;
2. the complete event hierarchy in bounded pages;
3. structured parameters for one exact event;
4. all or selected long-form Warp Metrics for one exact pass/marker range;
5. the shader inventory and static/dynamic facts for one exact range;
6. one shader's profile, instruction mix, source/DXIL rows, and SASS address correlation where
   available;
7. dynamic range-level instruction categories with samples and stall attribution;
8. range/frame Trace Analysis facts and raw counter rows where the Viewer exposes them.

No atom exposes GUI actions. Each scoped atom re-establishes and verifies its exact EventKey.
Trace Analysis and counter export take an explicit real pass/marker EventKey as their seed scope;
they never guess a hidden initialization row.

## Acceptance

- stdout is one schema-versioned JSON result for success and failure;
- every success records trace identity, exact Viewer version/build/SKU, Qt/bridge version, operation
  projection, and selected scope;
- collections have deterministic ordering and exact paging closure;
- duplicate marker/metric names remain distinct through stable key, occurrence, and source ordinal;
- unknown, unavailable, missing PDB, Pro-only SASS, and unsupported draw PC scope are explicit;
- event and metric atoms close against both real reports and the committed probe oracle;
- the generated metric fixture closes 18 values against the official Viewer counter export;
- the 30-frame fixture closes 306,576 events and its final six-node page;
- frame 0 and frame 29 GBuffer selections retain different EventKeys and the known 258/376 changed
  logical metrics;
- repeated calls retain only the session's startup-recorded CrashReporter guardian; no atom may
  create an additional/replacement guardian, and an asynchronous target model is required before
  graceful Viewer shutdown;
- no product JSON contains Qt class names, object names, pointers, widget coordinates, or generic
  probing settings.

## Information budget

For the first comparative workflow, a normal pass investigation should reuse Viewer sessions and
require:

- explicit resolution of target and baseline EventKeys;
- one `inspect-pass` fact package per side;
- one bounded `compare-ranges` result, with metric pages or exact table filters as needed;
- a focused shader/source call only after comparison identifies a ShaderKey worth drilling into.

Call count and elapsed time are measured during dogfood rather than hidden by the wrapper contract.

## Out of scope

- Graphics Capture and replayable resource contents;
- pixel history or proof of visual correctness;
- live capture/trace collection;
- Nsight Perf SDK;
- proprietary report parsing and private RPC;
- license bypass or reconstruction of missing PDB/source data;
- black-box bottleneck or root-cause operations.
