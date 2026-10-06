# Install handoff and local checks

The package supplies the CLI and this Skill. It does not contain NVIDIA Viewer,
Qt development files or the installed SolidProbe hook. Required components are:

| Component | Required value |
|---|---|
| Host | Windows x64 |
| CLI runtime | .NET 9 x64 bundled in the self-contained package; no separate runtime or SDK |
| Nsight Graphics Viewer | 2026.2.0.0 build 37991608 or 2026.3.1.0 build 38722833; public-release SKU |
| Loaded Viewer Qt | 6.8.1 for 2026.2; 6.10.2 for 2026.3.1 |
| SolidProbe bridge | probe-0.52, compiled with Qt 6.8.1 |

Run `nsight-analyzer.exe doctor --workspace <absolute-task-directory>` before
investigating a setup failure. For a non-default existing installation, add
`--viewer <absolute-path-to-ngfx-ui.exe>` on doctor and report operations.
Doctor reads file metadata and returns each dependency's expected value,
observed value, path and state. It does not start Viewer, create directories or
test write permission. `localPrerequisitesPresent` is only a local preflight;
`runtimeValidation: notChecked` remains until a real report operation verifies
the exact decoder/bridge, report identity and requested scope.

Default discovery checks 2026.2 first, then 2026.3.1, preserving the original decoder when both are
installed. Their default roots are `Nsight Graphics 2026.2.0` and `Nsight Graphics 2026.3.1` under
`C:\Program Files\NVIDIA Corporation`; both use
`host\windows-desktop-nomad-x64\ngfx-ui.exe`.
The bridge is expected at `Plugins\generic\solidprobe.dll` next to that executable.
Do not substitute another patch/build or bridge version merely because its name begins with 2026.

For a missing bridge, hand the package's `version` result and doctor's checks to
the person maintaining the matching NSightAnalyzer checkout. The maintained
source build/install entrypoint is
`tools/probes/qt-model-bridge/build-and-install.ps1` in that checkout, with its
instructions in the adjacent README. It requires the Qt 6.8.1 development environment and MSVC
build tools; Qt 6 permits this older-minor release plugin to load in the verified 6.10.2 runtime.
The script checks the exact Viewer compatibility entry and bridge pin and supports per-Viewer
backup/rollback. Exact entries come from the CLI's embedded host table; adding a verified patch to
an existing semantic profile does not copy adapter code. The independent package intentionally does not expose the probe
tool's generic model or experimental controls as product operations.

Installing/replacing that hook modifies the NVIDIA installation and is a
separate setup action requiring the user's explicit request. Doctor never does
it. If the bridge source/build environment is unavailable, report the missing
component and maintainer handoff; retrying report operations cannot repair it.

The packaged executable is a self-contained Windows x64 binary. It must not request a .NET runtime
installation. If a development build is run through `dotnet`, that checkout still needs the matching
SDK/runtime; do not confuse that developer prerequisite with the packaged Skill. Do not interpret
empty stdout as an empty trace result.

Use the same absolute `--workspace` for all calls in an investigation. It maps
runs to `<workspace>/.local/runs` and sessions to `<workspace>/.local/sessions`.
The override affects only that CLI process and its children; it does not change
the working directory or user/machine environment. Omitting it retains the
existing `NSIGHT_ANALYZER_RUN_ROOT` and `NSIGHT_ANALYZER_SESSION_ROOT` overrides,
or defaults to the current working directory. Close every opened report using
the same workspace and Viewer override.

`version` verifies the package files against `PACKAGE.json` and reports the
observed content fingerprint. `packageState: verified` means those files match
the local manifest, not that the package is signed or the Viewer runtime is ready.
An unpackaged development build reports its assembly fingerprint instead.
