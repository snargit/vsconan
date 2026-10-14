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
2. **Tools > Conan > Select Profiles...** lists the profiles from `conan profile list`. Pick the
   host profiles to install and the build profile (`-pr:b`). The choice is saved per user in
   `.vs/VsConan/settings.json`.
3. **Tools > Conan > Install Dependencies** runs this for each selected profile:

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
  paused until profiles are chosen.
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
F5. A Debug build inside Visual Studio deploys the extension into that Visual Studio's
experimental instance (root suffix `Exp`). The **Experimental Instance** profile in
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
