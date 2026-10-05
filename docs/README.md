# Documentation

- [Survey](survey.md): what real GPU Trace probing proved and the remaining hard boundaries.
- [Goal](goal.md): the atom-layer completion target and acceptance criteria.
- [Architecture](architecture.md): semantic DAG, Viewer-hosted adapter, contracts, and ownership.
- [Capability matrix](capability-matrix.md): one evidence-backed status per atomic fact family.
- [Agent interface comparison](agent-interface-matrix.md): RenderDoc MCP interaction patterns,
  current coverage, scoped gaps, and explicit Graphics Capture exclusions.
- [Performance data](performance-metrics.md): metric, shader, instruction, and scope semantics.
- [Roadmap](roadmap.md): atom-first implementation order, then wrappers and dogfood.
- [Probe corpus](probe-corpus.md): local real reports and fixed oracle facts.
- [Verification](testing.md): offline contract tests and optional real-Viewer closure.
- [Comparative hotspot workflow](comparative-hotspot-workflow.md): resolve, inspect, compare, and
  evidence-weighing boundaries for the first dogfood path.

The committed probe README remains the detailed low-level experiment log. Product documentation
does not promote generic Qt model access into a public operation.

The current catalog has 14 atoms, nine deterministic wrappers and three discovery commands (`describe`, `doctor`, `version`). `capabilities` is the
machine-readable source of truth and labels each entry by layer; the matrix records
evidence and boundaries, not a second hand-maintained API schema.
