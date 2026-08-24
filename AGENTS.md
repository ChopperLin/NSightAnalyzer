# NSightAnalyzer engineering rules

NSightAnalyzer retrieves performance facts from existing .ngfx-gputrace reports for Agents.
The currently supported decoder is the matching Nsight Graphics Viewer, not a documented offline
report SDK.

1. Scope is GPU Trace only. Do not add Graphics Capture, Nsight Perf SDK, live collection, replay,
   Aftermath, Nsight Systems, or Nsight Compute to the product path without a new user-approved
   scope decision backed by a real fixture.
2. The proven source is a version-pinned Viewer-hosted adapter. It may load the committed
   QGenericPlugin into the matching Viewer and read the decoded Qt item-model surfaces. Every
   result must identify this source as unsupported/version-pinned and record the exact Viewer
   product version, build, SKU, Qt version, and bridge schema.
3. Do not parse the proprietary .ngfx-gputrace container, call or inspect ngfx-rpc, bypass a
   product/license boundary, scrape screenshots, or expose mouse coordinates, widget names,
   QModelIndex, Qt model classes, internal pointers, or generic model-dump controls as product
   operations. Version-specific activation and selection stay inside the adapter.
4. Facts flow downward:

       Agent Skill -> deterministic wrapper -> atomic operation
                   -> NsightViewer2026_2 adapter -> Viewer bridge -> decoded report models

   Diagnosis, causality, optimization priority, confidence, and recommendations stay in the Skill.
   Wrappers only resolve identities, page, scan, filter, order, join, compare, and summarize exact
   facts.
5. Build atoms before product wrappers. A product atom is admitted only for a task-independent fact
   already proven by the frozen probe oracle and a real or sanitized fixture. Temporary verification
   scripts may compose atoms but are not wrappers.
6. Atom requests are semantic and self-contained. Expose range.metrics(eventKey), not
   select-event or click-panel. The adapter must establish the requested scope, wait for the
   required model to stabilize, and verify that the final selection still matches the exact key.
   A call must not depend on a previous call's GUI selection.
7. tools/probes/qt-model-bridge is the probing/legacy oracle. Generic model catalog/export,
   arbitrary role enumeration, all-column overrides, unsafe pointer calls, and experimental UI
   actions remain there. Product code extracts only the smallest proven semantic path and closes
   its output against this oracle.
8. No provider interface, generic query engine, or abstraction for hypothetical Viewer versions.
   The first implementation is a direct NsightViewer2026_2 adapter pinned to build 37991608.
   Add an interface only after a second real implementation exists.
9. Preserve unknown, absent, empty, not-loaded, unavailable, unsupported-scope, and
   license/input-limited as distinct states. Missing PDB HLSL, Standard-edition SASS text, and
   unreliable single-draw PC-sampling scope must never become successful empty data.
10. Every fact carries trace identity, exact selected scope, source provenance, decoder version/build,
    and bridge schema. Repeated marker names and duplicate metric headers retain occurrence and
    source ordinal; no value is silently overwritten.
11. Collections have stable ordering, cursor/limit paging, total/returned counts, and explicit
    truncation. Inputs, output, Viewer runtime, model traversal, artifact writes, and shutdown are
    bounded.
12. Stdout is exactly one versioned JSON result. Viewer diagnostics and run artifacts stay under
    .local/ or a caller-provided local directory. Exit code zero, an output file, or a stable model
    alone is not proof of success; validate request identity, trace identity, decoder build, schema,
    scope, completeness, freshness, and CrashReporter absence.
13. Viewer session reuse is transport only. It may amortize report loading, serialize GUI-thread
    requests, recover from crashes, and clean up processes, but it creates no semantic facts and
    cannot change an atom contract.
14. Keep changes small and fixture-driven. Promote the smallest missing objective fact into an atom
   only when dogfood proves it is missing. Move search policy or diagnostic judgment upward.
15. Trace Analysis and built-in counter export require a real pass/marker EventKey as an explicit
    seed scope. Do not hide a guessed event selector inside the adapter. Asynchronous actions must
    require their target model before completion and must not gracefully shut down the Viewer while
    background analysis is still active.
16. Counter export is the only current atom family with `writesLocalArtifacts` effect. Retain the
    validated `.xls`/TSV evidence under the isolated run directory. Delete only the Viewer-created
    trace copy after resolving that exact run-local path and validating its byte length; never touch
    the caller's report.
