# Prepare and repair the reader

The release package contains the self-contained CLI, this Skill, the matching
`bridge/solidprobe.dll`, its Viewer host table, and
`scripts/ensure-viewer-bridge.ps1`. Consumers do not need the source checkout,
Qt development files, MSVC, or a separate .NET runtime. NVIDIA Viewer itself is
not included and must already be installed.

| Component | Required value |
|---|---|
| Host / helper | Windows x64 / PowerShell 7 |
| CLI runtime | .NET 9 x64 bundled in the package |
| Viewer | 2026.2.0.0 build 37991608 or 2026.3.1.0 build 38722833; public-release |
| Viewer Qt | 6.8.1 for 2026.2; 6.10.2 for 2026.3.1 |
| Bridge | probe-0.52, compiled with Qt 6.8.1 |

## Before opening a report

Run the packaged helper once per task, using the same absolute workspace as all
report operations:

```powershell
pwsh -NoProfile -File <skill-directory>/scripts/ensure-viewer-bridge.ps1 -Workspace <absolute-task-directory>
```

It prefers an installed 2026.2 Viewer, then 2026.3.1, matching CLI discovery.
For another installation path, pass `-ViewerPath <exact-ngfx-ui.exe>` and use
that same executable as `--viewer` for every report operation and session close.
`-CheckOnly` reports `current`, `missing`, or `mismatch` without modifying files.

The helper verifies the release manifest and Viewer/Qt file metadata. It compares
the installed DLL's SHA-256 with the bundled DLL, so a rebuild with an unchanged
protocol label is still detected. If bytes match, it returns `state: current`
without writing files. Otherwise it stages the new DLL next to the destination,
verifies a backup of the previous bytes, replaces only `Plugins/generic/solidprobe.dll`,
and verifies the installed hash. Backups stay under the task workspace's
`.local/bridge-backups/<viewer-identity>/`; names contain the old DLL's hash, not
the new protocol version. A failed post-install check restores the prior state.

Routine bridge repair is part of using this Skill for requested trace analysis;
do not add another confirmation for this bounded, reversible prerequisite.
Announce the repair and continue within the environment's permissions.

| Helper result | Action |
|---|---|
| `current` / `installed` | Continue with the original report operation |
| `setup.package_invalid` / `setup.package_incomplete` | Obtain the complete matching release; do not copy an unverified DLL |
| `setup.viewer_missing` / `setup.viewer_unsupported` | Report the supported versions and observed installation; do not download/replace NVIDIA Viewer automatically |
| `setup.viewer_in_use` | Close only owned analyzer sessions with `viewer-session.close`; retry once. If a user's Viewer remains open, ask them to close that instance; never kill it |
| `setup.install_busy` | Another helper owns the install lock; retry after it finishes |
| `setup.install_failed` | Read the exact error. Permission denied requires installation write access; a locked file requires its owner to release it. Do not silently elevate or repeat unchanged |

The helper returns `runtimeValidation: notChecked`: file metadata cannot verify
the exact runtime build/SKU. A real report operation must validate the pinned
Viewer, bridge, report identity, and requested scope. If runtime still reports a
mismatch while the installed hash is current, stop reinstalling and report the
expected/observed versions. Never loosen the pins to force a report through.

## Diagnostics and lifecycle

`nsight-analyzer.exe doctor --workspace <absolute-task-directory>` is read-only.
It checks local file presence/metadata, not the loaded bridge version or its hash.
`localPrerequisitesPresent: true` is not a substitute for the helper or a successful
report read. `version` verifies package file hashes; it does not prove runtime
readiness or a signed release. Neither command installs anything.

Use the same `--workspace` on every report call and close. Report artifacts and
reusable sessions live under its `.local/runs` and `.local/sessions`. Close each
report with `viewer-session.close <trace> --workspace <same-path>` and the same
optional `--viewer`. If a repair changes the installed bridge, existing owned
sessions must be closed before retrying; already-loaded DLLs do not update in place.

For maintainers, `scripts/build-package.ps1` builds both CLI and bridge and
publishes the complete release. The bridge build uses the source checkout's
`tools/probes/qt-model-bridge/build-and-install.ps1 -SkipInstall` (Qt 6.8.1/MSVC).
Do not send an end user through that developer build path.
