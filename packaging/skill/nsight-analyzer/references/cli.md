# Shell and result contract

Use the adjacent executable's absolute path. Commands have the form
`nsight-analyzer.exe <operation> [arguments] --workspace <absolute-directory>`.
Quote paths and exact names that contain spaces. Prefer an argument array when
calling from another program; do not build a shell command from trace/marker text.

`capabilities` is the short directory. `describe <operation>` describes its
parameters and conditional constraints. `exactlyOneOf` requires one listed
option; `requires` requires the named option; `requiresValue` requires that value
in the named option's comma-separated values, including its declared default.
Single-value options cannot be repeated. Only options marked `repeatable` accept
multiple occurrences. `--pretty` and the legacy `--compact` cannot be combined.

PowerShell example (identities must come from this trace's earlier results):

```powershell
& $nsa compare-ranges $trace --event-ordinal $targetOrdinal `
  --baseline-trace $baselineTrace --baseline-event-ordinal $baselineOrdinal `
  --workspace $workspace
```

For timing only, use `--sections timing` and omit metric/shader options.
For several already resolved comparisons, `compare-timings --pairs-file <path>`
accepts a local **UTF-8** JSON object with `pairs`: 1..32 objects containing
`targetEventOrdinal`, `baselineEventOrdinal`, optional `label` and optional
`sampleGroup`, at most 64 KiB in total. Use returned identities from the respective
traces. Both optional strings are caller metadata, limited to 120 characters;
`sampleGroup` must be nonblank when supplied. Neither proves a capture setting.

Write the file with explicit UTF-8 in Windows PowerShell 5.1 and PowerShell 7;
the Windows PowerShell default `>`/`Out-File` encoding is unsuitable:

```powershell
$request = @{ pairs = @(
    @{ targetEventOrdinal = $targetOrdinal; baselineEventOrdinal = $baselineOrdinal }
) }
[IO.File]::WriteAllText($pairsPath, ($request | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
& $nsa compare-timings $trace --baseline-trace $baselineTrace `
  --pairs-file $pairsPath --workspace $workspace
```

Only pairs in the requested `--cursor`/`--limit` page are evaluated; `sourceOrdinal`
retains the original pair-file index, and `statisticsBasis: currentPage` applies
to every returned summary. Default summaries use the exact parent tree paths,
object, thread, description and depth of both endpoints
(`groupingBasis: exactTargetAndBaselineContext`). For explicitly comparable
samples across frames, set the same `sampleGroup` on their pair objects; this is
a caller-declared grouping (`groupingBasis: callerDeclaredSampleGroup`), still
requiring equal event names and depths. Check those grouping fields before
treating a median as repeated measurements of the same work.

If an all-pairs summary is needed, request a single page large enough for the
supplied file (at most 32 pairs). Do not average page medians to obtain a global
median. The CLI never chooses or infers matching scopes.

For a known metric, use `find-metrics ... --name-contains <name>` or
`--source-ordinal <returned-ordinal>`, exactly one. For sampled source rows,
`find-source-hotspots` requires the exact ShaderKey plus one `--view`; it reports
coverage only for that view.

Python caller example:

```python
completed = subprocess.run(
    [nsa, "find-ranges", trace, "--name-contains", marker,
     "--workspace", workspace, "--timeout-ms", "180000"],
    capture_output=True, text=True, encoding="utf-8", timeout=210,
)
envelope = json.loads(completed.stdout)  # Parse failure JSON even when exit != 0.
result = envelope["result"]
if not result["isSuccess"]:
    recovery = result["error"].get("recovery")
```

Imports and variables above belong to the caller. If stdout is empty or invalid,
inspect captured stderr for a host/startup failure; do not manufacture success.
Allow the CLI's declared timeout plus shutdown time before the caller kills it.
In `cmd.exe`, quote the executable as well as each path, for example:

```bat
"C:\tools\nsight analyzer\nsight-analyzer.exe" describe find-metrics --workspace "D:\capture review"
```

Stdout is one JSON envelope: `operation`, `schemaVersion`, `result`. Check the
supported major version and `result.isSuccess` before using `result.value`.
Failure details are under `result.error`, with recovery under
`result.error.recovery`. Exit codes are 0 success, 2 invalid input, 3 not found,
4 unsupported, 5 unavailable, 6 adapter mismatch, 7 trace error, 8 Viewer error,
9 timeout and 10 internal/output-bound error. A successful doctor means its
checks completed, not that all dependencies passed or Viewer was opened.

Collections carry `totalCount`, `returnedCount`, `truncated` and `nextCursor` at
their own collection node. Reissue the same request with that cursor only when
more items are needed. `compare-ranges` defaults to changed/unmatched metric
rows; its page total differs from the overall matched/comparable counts.
Keep scope, source provenance, availability, numeric state and coverage with
selected evidence. Null/omitted/unavailable values never become zero.

Stdout JSON is capped at 1 MiB. Shader rows vary substantially in serialized
width, especially with `--detail`, so `trace.range-shaders` automatically returns
the largest stable row prefix that fits. When this happens, it emits warning
`runtime.response_page_reduced`, keeps the requested `limit`, and sets
`returnedCount`, `truncated`, and `nextCursor` to the actual page. Continue from
that cursor with the same filters and repeat until `nextCursor` is absent. The byte
bound is a transport constraint, not evidence of a damaged report, and paging never
modifies the input report.

When work ends, close each report with `viewer-session.close <trace>` using the
same `--workspace` and optional `--viewer`. `version` is sufficient for recording
which CLI build/package was used; do not repeat the full capability directory.
