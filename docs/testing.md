# Verification strategy

## Test layers

1. Contract tests run without Nsight or a GPU against sanitized bridge JSON.
2. Projection tests close semantic pages against fixed raw probe output.
3. Optional Viewer integration opens a caller-supplied real trace and validates exact build,
   selection, model stability, output freshness, and CrashReporter absence.
4. Oracle closure compares product atom output to probe 0.44 on the same trace and selector.

Generic model discovery is never part of product verification.

## Fixed real-trace oracles

### Generated validation trace

- 18 register, SysL2, and VRAM values across two markers exactly equal the official
  GPUTRACE_REGIMES.xls export.

### Real single-frame trace

- GBuffer and Deferred ranges return different register/L2/VRAM values;
- draw event 1661 returns InstanceCount 94206 and StartInstanceLocation 262176;
- shader c709 returns 3,592 samples, 24 warps, 30 registers, 14 live registers, 544 static
  instructions, and 1,501 Long Scoreboard samples;
- its source projection contains 480 DXIL rows and 242 SASS address rows.
- range Instruction Mix contains 56 categories; pipe `FMA`, family `FP32 Math` has 61,179
  instructions and 10,343 samples for the tested GBuffer range;
- built-in export contains 226 ranges and 646 counters, removes its generated trace copy, and
  reports GBuffer PS register allocation 62.7392% versus Warp Metrics 62.739219%.

### Real 30-frame trace

- event total 306,576;
- offset 306570, limit 10 returns six nodes, hasMore false, ending in Present event 276761;
- frame 0 and frame 29 GBuffer each return 88 tables and 409 rows;
- 258 of 376 unique logical metrics change;
- frame-0 GBuffer returns 1,020 shader hashes, 133 sampled, and 27,432 shader samples.
- Trace Analysis returns 30 frames; frame 0 is 25.49 ms with top issues GPU Engine Activity 70.1%,
  VRAM Limited 10.1%, and L1TEX Long Scoreboard 9.2%.

## Atom gates

Every collection test verifies total, returned, cursor, next cursor, truncation, and concatenated-page
closure. Duplicate names remain distinct. Unknown properties and invalid bounds fail.

Every scoped atom verifies the final observed EventKey equals the requested key. A stable metric
snapshot under the wrong selection is a failure.

Every real run verifies:

- exact Viewer product version/build and bridge presence;
- fresh artifact with matching requestId and trace reportId;
- expected schema and complete status;
- bounded elapsed time and output;
- Viewer exit or controlled session state;
- zero newly spawned CrashReporter processes.

## Safety

Real traces, shader source, raw model output, Viewer logs, and generated counter exports remain in
.local/. Verification only removes run directories it created and resolved under that root. It never
deletes or overwrites the caller's report.
