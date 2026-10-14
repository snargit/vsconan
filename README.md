# VsConan

A Visual Studio 2022 / 2026 extension that installs [Conan 2](https://conan.io) dependencies for a
C++ CMake project, once per Conan profile, and hands the result to Visual Studio's built-in CMake
integration to configure and build.

## Which folders VsConan manages

VsConan only acts on a folder opened with **File > Open > Folder** whose root contains both:

- a `conanfile.py` or `conanfile.txt` (the only files `conan install` accepts), and
- a `CMakeLists.txt`.

For any other folder, VsConan does nothing: it runs no Conan commands, writes no files, and
hides the **Tools > Conan** menu. Other setups include `.sln` solutions, plain CMake projects,
folders with only `conan.lock` or `conandata.yml`, and folders whose conanfile or
`CMakeLists.txt` is in a subfolder.

There is one exception. If a folder VsConan paused stops qualifying (for example, the conanfile
was deleted), VsConan resumes CMake for it on the next open.

## How it works

1. Open a folder containing a `conanfile.py` or `conanfile.txt` and a `CMakeLists.txt`
   (**File > Open > Folder**).
2. The first time the folder opens, VsConan pauses CMake and shows a window. It lists the
   profiles from `conan profile list`. Pick the host profiles to install and the build profile
   (`-pr:b`), then choose:
   - **Install** saves the choice per user in `.vs/VsConan/settings.json` and runs step 3.
   - **Not now** resumes CMake without installing anything.

   Either way, the window isn't shown again for that folder. To change the profiles later, use
   **Tools > Conan > Select Profiles...**.
3. VsConan runs this for each selected profile. **Tools > Conan > Install Dependencies** runs it
   again on demand:

   ```
   conan install <folder> -pr:h <profile> -pr:b <build profile> --build=missing --format=json
         -c tools.cmake.cmake_layout:build_folder_vars=['const.<profile>']
         -c tools.cmake.cmaketoolchain:generator=Ninja
         -c tools.cmake.cmaketoolchain:user_presets=
   ```

   Output goes to the **Conan** pane of the Output window.
4. Each install writes that profile's `CMakePresets.json`. When all profiles are done, VsConan
   writes a single `CMakeUserPresets.json` that includes them. Visual Studio reads that file, so
   each profile appears as a `conan-<profile>-<build type>` configuration. Visual Studio then
   runs the configure and build.
5. After a folder's first successful install, an info bar offers **Switch to CMake Targets View**.
   This view lists the project's CMake targets in Solution Explorer, which suits a CMake project
   better than the default Folder View. The link runs the CMake package's own view-switch
   command. If that isn't possible, the Conan output pane explains how to switch with Solution
   Explorer's **Switch Views** button.

### Ordering: Conan runs before CMake configures

Visual Studio watches the presets and toolchain files, and Conan rewrites them during an install.
Left alone, the CMake integration would reload and reconfigure in the middle of an install, once
for each file Conan touches. VsConan prevents this in three ways:

- **CMake is paused during installs.** Before running Conan, VsConan sets `"enableCMake": false`
  in `.vs/CMakeWorkspaceSettings.json` (Visual Studio watches this file). It sets it back to
  `true` once everything is written, so Visual Studio configures once against the final state.
  If you disabled CMake yourself, VsConan leaves it disabled. Other settings in the file are kept.
- **`CMakeUserPresets.json` is written once.** Conan's per-install update of the file is turned
  off (`user_presets=`). VsConan writes the file itself, as a single file replacement, and only
  when the contents change. A hand-written `CMakeUserPresets.json` (one without a `vendor.conan`
  key) is never overwritten.
- **Folders start paused.** There's no "before folder open" event, so the CMake integration could
  start before VsConan loads. Instead, VsConan pauses CMake when a managed folder *closes*. On the
  next open it compares a fingerprint of the conanfile, `conan.lock` and the selected profiles
  with the last successful install. It runs Conan only if they differ, then resumes CMake.

Some cases are best-effort:

- **First open of a fresh clone.** VsConan hasn't paused this folder yet, so the CMake
  integration may start before VsConan loads. VsConan pauses it as soon as it loads, and keeps it
  paused while the first-run window is open.
- **Folder closed during an install, or VS exits.** The install is cancelled and CMake stays
  paused, so the next open finishes the install first.
- **Profile edits.** The fingerprint doesn't include the contents of Conan profiles. Run
  **Install Dependencies** after editing one.
- **Folder opened without VsConan.** If you uninstall or disable the extension, a managed folder
  can be left with `"enableCMake": false`. Remove the setting from
  `.vs/CMakeWorkspaceSettings.json`, or use **Project > CMake Workspace Settings**.

### Requirements for the consuming project

- The conanfile must use `cmake_layout` (in `[layout]` or `layout()`). The profile name is added
  to the build folder and preset names through `build_folder_vars`. Without `cmake_layout`,
  presets from different profiles would have the same name.
- The conanfile must use the `CMakeToolchain` and `CMakeDeps` generators.
- The `compiler.version` in each MSVC profile must match a toolset that's installed (e.g. 194 for
  VS 2022, 195 for VS 2026). Conan writes that toolset into the preset.

## Automation

VsConan's commands have fixed names and accept arguments. So anything that can drive Visual
Studio through its automation object (`EnvDTE.DTE`) can select profiles, install dependencies
and read the status, without any UI. That includes scripts, other extensions and MCP servers.
VsConan doesn't ship an MCP server itself; this section is the contract for writing one.

### Commands

| Command | Arguments | Effect |
| --- | --- | --- |
| `Conan.ShowStatus` | `[/id:<token>]` | Reports the folder's status: selected profiles, whether the last install is up to date, whether CMake is paused, the `conan-*` configure presets, and `conan profile list`. Also prints it to the **Conan** output pane. |
| `Conan.SelectProfiles` | `<host profile>... [/build:<profile>] [/id:<token>]` | Saves the profiles to install, checked against `conan profile list`. It doesn't install; run `Conan.InstallDependencies` next. |
| `Conan.InstallDependencies` | `[/force] [/id:<token>]` | Runs the installs for the saved profiles and writes `CMakeUserPresets.json`. Visual Studio then configures. `/force` reinstalls even if nothing has changed. |

Argument syntax:

- Host profiles are separated by spaces or commas. Switches start with `/`, and unknown switches
  are reported as errors.
- **Any** argument switches the command to automation mode: no dialogs, and the outcome goes to
  the result file. To run `Conan.InstallDependencies` or `Conan.ShowStatus` with nothing else
  to say, pass `/id:<token>`.
- Without arguments, `Conan.InstallDependencies` and `Conan.SelectProfiles` behave exactly like
  the **Tools > Conan** menu items, dialogs included.

The commands are only enabled while the open folder is one VsConan manages. That means a
folder opened with **File > Open > Folder** whose root has a conanfile and a `CMakeLists.txt`.
Otherwise `ExecuteCommand` fails with "Command ... is not available". `ExecuteCommand` also
can't run while a modal dialog is open, such as VsConan's first-run window.

### Results

Visual Studio commands can't return values, and installs take a while. So each command run with
arguments writes `<folder>/.vs/VsConan/command-result.json`. It writes the file when the command
starts, and again when it finishes:

```json
{
  "id": "42",
  "command": "Conan.InstallDependencies",
  "state": "succeeded",
  "startedAt": "2026-10-14T16:20:01.123+01:00",
  "completedAt": "2026-10-14T16:20:31.456+01:00",
  "result": {
    "outcome": "installed",
    "profiles": [ { "profile": "default", "succeeded": true, "exitCode": 0 } ],
    "configurePresets": [ "conan-default-release" ]
  }
}
```

- `state` is `running`, then `succeeded` or `failed`. When it's `failed`, `error` holds a message
  meant for the caller, such as `Unknown Conan profile(s): clang. Available profiles: default, debug.`
- `id` echoes `/id:<token>`. Only the most recent command's result is kept, so poll until both
  `id` matches your token and `state` isn't `running`.
- `result` depends on the command:
  - `Conan.ShowStatus` and `Conan.SelectProfiles` give a **status**: `folder`, `conanfile`,
    `usesCmakeLayout`, `hostProfiles`, `buildProfile`, `upToDate`, `cmakePaused`,
    `configurePresets` and `availableProfiles`.
  - `Conan.InstallDependencies` gives an **install report**: `outcome` (`installed`, `upToDate`,
    `partiallyFailed` or `userPresetsNotOwned`), `profiles` and `configurePresets`.

Each command also writes a one-line summary to the **Conan** output pane.

### Example

From Windows PowerShell 5.1, which provides `Marshal.GetActiveObject`. Use
`VisualStudio.DTE.17.0` for VS 2022 and `VisualStudio.DTE.18.0` for VS 2026.

```powershell
$dte = [Runtime.InteropServices.Marshal]::GetActiveObject('VisualStudio.DTE.18.0')
$folder = Split-Path $dte.Solution.FullName   # in Open Folder mode, this is the opened folder

function Invoke-Conan($command, $arguments) {
    $id = [guid]::NewGuid().ToString('N')
    $dte.ExecuteCommand($command, "$arguments /id:$id")
    $path = Join-Path $folder '.vs\VsConan\command-result.json'
    do {
        Start-Sleep -Milliseconds 500
        $result = Get-Content $path -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
    } until ($result.id -eq $id -and $result.state -ne 'running')
    $result
}

Invoke-Conan 'Conan.ShowStatus' ''
Invoke-Conan 'Conan.SelectProfiles' 'default debug /build:default'
Invoke-Conan 'Conan.InstallDependencies' ''
```

`GetActiveObject` returns whichever instance of that Visual Studio version registered first. To
target a particular instance, look up its `!VisualStudio.DTE.<version>:<process id>` entry in the
Running Object Table. MCP servers that work with several instances need to do this.

## Repository layout

| Path | Description |
| --- | --- |
| `src/VsConan.Core` | `netstandard2.0` library with no VS dependencies: Conan CLI wrapper, install orchestration, settings. |
| `src/VsConan` | The VSIX (VSSDK, in-process, .NET Framework 4.8): package, menu commands, output pane, profile dialog. |
| `tests/VsConan.Core.Tests` | xUnit tests for the core library. `Category=Integration` tests need `conan` on PATH. |

## Building

The VSIX needs full MSBuild, from a Visual Studio 2022+ installation with the
**Visual Studio extension development** workload:

```sh
msbuild VsConan.sln -restore -p:Configuration=Release
```

The package is written to `src/VsConan/bin/Release/net48/VsConan.vsix`.

### Debugging

To debug, open `VsConan.sln` in VS 2022 or VS 2026, make `VsConan` the startup project, and press
F5. This deploys the extension into that Visual Studio's experimental instance (root suffix `Exp`)
and launches it. `VsConan` is listed first in the solution so that it becomes the startup project
by default. If Visual Studio has already saved a different startup project, right-click
**VsConan** and choose **Set as Startup Project**.

Deploying and launching work differently in each version:

- **VS 2026.** Visual Studio's built-in VSIX debugger deploys and launches the extension. It only
  deploys if **Deploy** is ticked for `VsConan` in **Build > Configuration Manager**.
  `VsConan.sln` ticks it for the Debug configurations.
- **VS 2022.** The Debug build deploys the extension. The **Experimental Instance** profile in
  `src/VsConan/Properties/launchSettings.json` then starts `devenv.exe /rootsuffix Exp`.

How the build is set up:

- **BuildTools version.** The project uses `Microsoft.VSSDK.BuildTools` 17.x under VS 2022 and
  18.x under VS 2026. The deploy tasks load the building Visual Studio's own assemblies, so a
  single version can't deploy to both.
- **Explicit SDK imports.** The 17.x tools need their targets imported after the .NET SDK's.
  That's why `VsConan.csproj` imports `Sdk.props`/`Sdk.targets` explicitly instead of using
  `<Project Sdk=...>`.
- **Deployment.** Command-line builds only produce the `.vsix`; they never deploy it.

If deployment fails with `VSSDK1031: Extension '...' could not be found`, the experimental
instance is stale. Reset it from a Developer Command Prompt for that Visual Studio, then press F5
again:

```
CreateExpInstance /Reset /VSInstance=<version>_<instance id> /RootSuffix=Exp
```

The `<version>_<instance id>` part is the experimental hive's folder name under
`%LOCALAPPDATA%\Microsoft\VisualStudio`, without the `Exp` suffix (for example, `18.0_4e9aa619`).

Core tests run with the .NET SDK alone:

```sh
dotnet test tests/VsConan.Core.Tests                                   # all tests
dotnet test tests/VsConan.Core.Tests --filter Category!=Integration    # skip tests that call conan
```

## Continuous integration and releases

GitHub Actions workflows live in `.github/workflows`:

- **`ci.yml`** runs on every push to `main`, on pull requests, and on demand. It builds the
  solution in Release with Visual Studio's MSBuild and runs all the tests. Conan is installed so
  the integration tests run too. The resulting `.vsix` is uploaded as the `VsConan-vsix` build
  artifact.
- **`release.yml`** runs when a `v*` tag is pushed. It sets the tag's version in the VSIX
  manifest and the assemblies, builds, and tests. It then creates a GitHub release with
  `VsConan-<version>.vsix` attached and release notes generated from the commits.

To make a release:

```sh
git tag v0.2.0
git push origin v0.2.0
```

Tags must be `vMAJOR.MINOR.PATCH`. VSIX versions are purely numeric, so pre-release suffixes like
`-beta` are rejected. The versions in the source tree (`0.1.0`) are only used for local builds;
releases take their version from the tag.
