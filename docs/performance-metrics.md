# GPU Trace performance data semantics

## Scope first

Every performance value belongs to an exact EventKey. Pass/marker ranges are the supported grain for
Warp Metrics and PC sampling. Event parameters may use a draw EventKey, but shader PC-sampling
claims must reject the known unreliable single-draw scope.

Atoms never expose mutable GUI selection. They select the requested key internally and return both
the requested and observed scope so stale data cannot masquerade as success.

## Range metrics

Warp Metrics are dynamic tables. They are represented as long-form values rather than fixed DTO
fields:

| Field | Meaning |
|---|---|
| tableName/tableOccurrence | exact table identity in stable discovery order |
| rowName/rowOccurrence | exact metric row and duplicate occurrence |
| columnName/columnOccurrence | value dimension such as Occupancy %, Bytes, or GB/s |
| sourceOrdinal | stable flattened ordinal before paging |
| displayValue | Viewer display text |
| preciseValue | Tooltip precision when present |
| valueType | original Qt value type projected to a safe name |
| unit | exact header-derived unit when separable |
| description | row description/tooltip |
| availability | available, absent, unavailable, or unknown |

NVIDIA names are preserved. NSightAnalyzer does not rename register allocation to AMD VGPR. A later
semantic catalog may map exact versioned metric names to higher-level evidence, but it is not an atom
source.

Two register concepts remain distinct:

- shader static register count from the shader row;
- range-level SM register-file allocation/average registers per SM from Warp Metrics.

## Shader facts

Range shader inventory returns stage, entry/name, exact hash, range-local pipeline context,
correlation availability, samples, warps, static registers, shared memory, CTA dimensions, live
registers, static instruction count, instruction-mix vector, dependency samples, and stall columns
as exposed by the safe view.

ShaderKey is stage plus hash, with range-local occurrence where duplicates exist. Rows without a hash
remain explicit internal/unattributed entries rather than fabricated shader identities.

The shader row's instruction vector is shader-specific. The separate InstructionMixModel is a
range aggregate exposed by `trace.range-instruction-mix`; it returns pipe/family/operation,
dynamic samples, instruction count, and the 17 named stall buckets.

## Source and instruction correlation

For an exact range and ShaderKey, the source atom may return:

- HLSL/function rows when matching PDB data exists;
- DXIL rows with samples, stalls, latency, instruction mix, dependency samples, and live registers;
- SASS addresses and SASS-to-DXIL association;
- full SASS opcode text only when the licensed Viewer surface actually exposes it.

Missing PDB and Pro-only SASS are capability states with diagnostics. They are not empty lists and
are never reconstructed from guesses.

## Raw counter export

The Viewer's counter export is a second projection of GPU Trace facts. Duplicate header names use
name plus occurrence plus source ordinal. Repeated marker names use EventKey/range occurrence.

The current export path may copy the original trace. Transport records every created artifact and
only cleans files it created under its run directory. User reports are never overwritten or deleted.
The tested export has one range-name column plus 646 counter columns (647 total columns), 226 rows,
and TSV encoding despite the `.xls` extension.
