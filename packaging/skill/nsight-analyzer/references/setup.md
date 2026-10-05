# Install handoff and local checks

The package supplies the CLI and this Skill. It does not contain NVIDIA Viewer,
Qt development files or the installed SolidProbe hook. Required components are:

| Component | Required value |
|---|---|
| Host | Windows x64 |
| CLI runtime | .NET 9 x64 runtime |
| Nsight Graphics Viewer | 2026.2.0.0, build 37991608, public-release SKU |
| Loaded Viewer Qt | 6.8.1 |
| SolidProbe bridge | probe-0.51 |

Run `nsight-analyzer.exe doctor --workspace <absolute-task-directory>` before
investigating a setup failure. For a non-default existing installation, add
`--viewer <absolute-path-to-ngfx-ui.exe>` on doctor and report operations.
Doctor reads file metadata and returns each dependency's expected value,
observed value, path and state. It does not start Viewer, create directories or
test write permission. `localPrerequisitesPresent` is only a local preflight;
`runtimeValidation: notChecked` remains until a real report operation verifies
the exact decoder/bridge, report identity and requested scope.

The default Viewer path is:
`C:\Program Files\NVIDIA Corporation\Nsight Graphics 2026.2.0\host\windows-desktop-nomad-x64\ngfx-ui.exe`.
The bridge is expected at `Plugins\generic\solidprobe.dll` next to that executable.
Do not substitute a different Viewer build or bridge version.

For a missing bridge, hand the package's `version` result and doctor's checks to
the person maintaining the matching NSightAnalyzer checkout. The maintained
source build/install entrypoint is
`tools/probes/qt-model-bridge/build-and-install.ps1` in that checkout, with its
instructions in the adjacent README. It requires the matching Qt 6.8.1 development
environment and MSVC build tools, checks the adapter's bridge pin and supports
backup/rollback. The independent package intentionally does not expose the probe
tool's generic model or experimental controls as product operations.

Installing/replacing that hook modifies the NVIDIA installation and is a
separate setup action requiring the user's explicit request. Doctor never does
it. If the bridge source/build environment is unavailable, report the missing
component and maintainer handoff; retrying report operations cannot repair it.

If the .NET host prints a runtime installation error before any JSON appears,
the CLI has not started. Resolve the x64 .NET 9 runtime dependency before using
doctor. Do not interpret empty stdout as an empty trace result.

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
