# NSightAnalyzer engineering rules

Build the smallest trustworthy bridge between Agents and NVIDIA's public Nsight surfaces.

1. Public evidence only. Product code may call documented NVIDIA executables, documented SDK APIs,
   or consume documented exports. Do not parse proprietary capture/report containers, automate the
   GUI, or depend on the private `ngfx-rpc` protocol.
2. Facts flow downward: Agent Skill -> deterministic wrapper -> atomic operation -> source adapter ->
   official NVIDIA surface. Diagnosis, causality, ranking policy, and recommendations stay above the
   atomic operation layer.
3. Every fact carries source provenance and the exact Nsight product version/build that produced or
   decoded it. A nominal SDK version is insufficient; capabilities are probed from the installed
   binaries, help text, headers, and parser fixtures.
4. Unknown, absent, empty, and unavailable are different states. Never turn an empty stdout stream,
   a missing column, `-1`, or a human warning into a successful empty fact set.
5. NVIDIA subprocess exit code zero is not proof of success. Validate the expected artifact or
   parseable output schema before returning success.
6. Stdout is exactly one versioned JSON response. Human diagnostics and captured NVIDIA logs go to
   stderr or an explicit artifact. All inputs, outputs, subprocess time, and artifact writes are
   bounded.
7. Treat capture metadata as sensitive. Process environment and command line are denied by default;
   never place them in an Agent response. Any future raw export is explicit, local, and documented.
8. Collections have stable ordering, cursor/limit paging, total/returned counts, and explicit
   truncation. `--compact` controls whitespace only.
9. Generated captures, traces, screenshots, raw exports, and perf reports belong under `.local/` or
   a caller-provided output directory and never enter Git.
10. Add an operation only for a task-independent fact with a fixture oracle. Compose repeated
    retrieval in wrappers. Do not add an interface until a second real implementation exists.
