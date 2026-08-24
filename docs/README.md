# Documentation

- [Survey](survey.md): what real GPU Trace probing proved and the remaining hard boundaries.
- [Goal](goal.md): the atom-layer completion target and acceptance criteria.
- [Architecture](architecture.md): semantic DAG, Viewer-hosted adapter, contracts, and ownership.
- [Capability matrix](capability-matrix.md): one evidence-backed status per atomic fact family.
- [Performance data](performance-metrics.md): metric, shader, instruction, and scope semantics.
- [Roadmap](roadmap.md): atom-first implementation order, then wrappers and dogfood.
- [Probe corpus](probe-corpus.md): local real reports and fixed oracle facts.
- [Verification](testing.md): offline contract tests and optional real-Viewer closure.

The committed probe README remains the detailed low-level experiment log. Product documentation
does not promote generic Qt model access into a public operation.

The current atom catalog has 11 operations. `capabilities` is the machine-readable source of truth;
the matrix records evidence and boundaries, not a second hand-maintained API schema.
