# Recover without repeating expensive work

Inspect `result.isSuccess`, then the stable error code and structured `recovery`.
Exit zero and a JSON file alone do not establish complete evidence. Preserve section
selection, page totals, truncation, source provenance and exact scope when resuming.

| Failure | Next action |
|---|---|
| Invalid arguments | `describe <operation>`; correct the stated parameter |
| Ambiguous name / wrong occurrence | `find-events` or `find-ranges` within the known ancestor; resolve exact context |
| Unsupported draw sampling | Select its real enclosing pass/marker; retain the draw for event parameters |
| Response too large | Reduce the requested page or shader Top-N; use an exact table filter, preserving coverage |
| Timeout | Narrow scope/tables/sections first. Increase the overall budget only for a known necessary request |
| Viewer transient error with `retryable: true` | Retry once; the transport rebuilds a lost session |
| Instruction Mix not loaded | Report that family unavailable; use metrics/shaders independently when they answer the question |
| Missing PDB or unavailable SASS | Use available correlation evidence and state the limitation; do not retry unchanged |
| Viewer/bridge/version mismatch | Run `doctor` with the same `--viewer` and `--workspace`; report its expected/observed/path checks and read setup.md. Do not install automatically |
| Different working directory / missing session | Repeat with the original absolute `--workspace` and `--viewer`; check doctor for resolved run/session roots |
| Dependency host failure with no JSON | Read native stderr and setup.md; the CLI cannot produce JSON before the .NET host starts |

Do not claim a completed comparison if a requested family failed. A narrower
request can succeed for its explicitly selected families, but cannot retroactively
make the unavailable family complete. If the report changed, rediscover its keys.
Use `version` or `--version` when reproducing a failure: schema 2.x and the same
Viewer do not establish that two clients have identical package contents.
