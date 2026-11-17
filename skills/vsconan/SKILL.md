---
name: vsconan
description: >-
  Use when working on a C++ CMake folder opened in Visual Studio (File > Open > Folder) whose root
  has a conanfile.py or conanfile.txt and a CMakeLists.txt, with the VsConan extension installed:
  adding, removing or upgrading Conan dependencies, changing conanfile options, conan.lock or
  conandata.yml, choosing Conan profiles, installing dependencies, or when Visual Studio's CMake is
  paused, has no conan-* configurations, or configures without the Conan toolchain. Installs go
  through VsConan's commands instead of running conan install in a shell.
license: MIT
---

# VsConan: Conan dependencies for a CMake folder in Visual Studio

VsConan installs a folder's Conan 2 dependencies once per selected Conan profile, then hands the
result to Visual Studio's CMake. It pauses CMake while Conan runs, and writes the
`CMakeUserPresets.json` that Visual Studio configures from. Each host profile gives
`conan-<profile>-<build type>` configurations, such as `conan-default-release`.

A `conan install` run in a shell skips all of that. Visual Studio reconfigures in the middle of
the install, and the presets VsConan publishes go stale. So **install through VsConan's commands,
never with `conan install`.**

VsConan only manages a folder opened with File > Open > Folder whose root has both a conanfile
and a `CMakeLists.txt`. For anything else (a `.sln`, or a conanfile in a subfolder) it does
nothing, and this skill doesn't apply.

## How to run VsConan's commands

VsConan has three Visual Studio commands, and they report through result files:

| Command | Arguments | Does |
| --- | --- | --- |
| `Conan.ShowStatus` | | Reports the selected profiles, the available ones, whether the install is up to date or running, whether CMake is paused, and the `conan-*` presets. |
| `Conan.SelectProfiles` | `<host profile>... [/build:<profile>]` | Saves the profiles to install for. Doesn't install. |
| `Conan.InstallDependencies` | `[/force]` | Installs for the selected profiles and waits for any install already running. Visual Studio then configures. |

Pick the first of these ways to run them that you have:

1. **Tools that wrap them.** An MCP server or other integration for Visual Studio may offer tools
   that run these commands, for example `conan_status` or `conan_install`. Use those, because
   they already wait for the result.
2. **This skill's script**, from a shell on the same Windows machine. Use Windows PowerShell 5.1
   or PowerShell 7, with `scripts/Invoke-VsConan.ps1` in this skill's folder:

   ```powershell
   .\scripts\Invoke-VsConan.ps1 ShowStatus -Folder <project folder>
   .\scripts\Invoke-VsConan.ps1 SelectProfiles -HostProfiles default,debug -BuildProfile default -Folder <project folder>
   .\scripts\Invoke-VsConan.ps1 InstallDependencies [-Force] -Folder <project folder>
   ```

   The script finds the Visual Studio that has the folder open, runs the command and prints the
   result JSON. It exits with 0 if the command succeeded and 1 if it failed (see `error`). It
   exits with 2 if the command couldn't run at all, and writes the reason to standard error.
3. **Neither.** Ask the user to run **Tools > Conan > Install Dependencies** (or **Select
   Profiles...**) in Visual Studio. The outcome is shown in the **Conan** pane of the Output window.

## Reading results

A result has `state` (`succeeded` or `failed`), `error` when it failed, and `result`:

- **Status** (`ShowStatus`, `SelectProfiles`): `hostProfiles`, `buildProfile`,
  `availableProfiles`, `upToDate`, `installing`, `cmakePaused`, `usesCmakeLayout` and
  `configurePresets`.
- **Install report** (`InstallDependencies`): `outcome` is `installed`, `upToDate`,
  `partiallyFailed` or `userPresetsNotOwned`. It also has `profiles` (each with `succeeded`, and
  `errorOutput` with the end of Conan's output when it failed) and `configurePresets`.
- **`hints`**, in either: what to do next. Follow them. When a hint says to ask the user, ask
  before doing it, because it's about the user's own files or settings.

## Adding or changing a dependency

1. **Find the reference.** For example, `conan search "fmt/*" -r conancenter` lists the versions.
   Searching like this, and other Conan commands that don't install into this folder, are fine to
   run in a shell.
2. **Edit the conanfile.**
   - In `conanfile.txt`, add a line under `[requires]` (`fmt/11.0.2`), or under
     `[tool_requires]` for build tools. Set package options under `[options]`
     (`fmt/*:header_only=True`).
   - In `conanfile.py`, call `self.requires("fmt/11.0.2")` in `requirements()`, or set
     `requires`.
   - If there's a `conan.lock`, update it too: run `conan lock add --requires=fmt/11.0.2`, or
     recreate it with `conan lock create .`, both in the project folder. Otherwise the install
     fails with *Requirement ... not in lockfile*, and version ranges keep resolving to the locked
     versions.
3. **Install with `Conan.InstallDependencies`.** If Visual Studio runs VsConan unattended, or the
   user chose **Always install automatically**, VsConan may already be installing because the
   conanfile changed. `InstallDependencies` waits for that install and then reports it.
4. **Use the dependency in `CMakeLists.txt`.** Call `find_package(<name> REQUIRED CONFIG)` and
   `target_link_libraries(<target> PRIVATE <name>::<component>)`. Conan prints the exact names
   after the line *CMakeDeps necessary find_package() and targets for your CMakeLists.txt* in the
   Conan output pane. They're also in the profile's generators folder, at
   `build/<profile>/<build type>/generators`:
   - every `<name>-config.cmake` or `<Name>Config.cmake` file there is a `find_package` name;
   - `conandeps_legacy.cmake` lists the targets of all direct dependencies.
5. **Build through Visual Studio.** It configures again by itself after the install.

To remove a dependency, delete it from both the conanfile and `CMakeLists.txt`, then install.

## Rules for the conanfile

VsConan needs these. If any are missing, the hints say so. Keep them when you edit the conanfile:

- **`cmake_layout`**: `cmake_layout(self)` in `layout()`, or `cmake_layout` under `[layout]`.
  VsConan names each profile's build folder and presets through it.
- **The `CMakeToolchain` and `CMakeDeps` generators**, under `[generators]` or in `generators`
  (or `generate()`). Without CMakeToolchain there are no presets for Visual Studio.
- **For MSVC, a `compiler.version` that matches an installed toolset** (194 for Visual Studio
  2022, 195 for Visual Studio 2026). Conan writes that toolset into the presets.

## Profiles

- Profiles live in the Conan home's `profiles` folder. To find the home, run `conan config home`
  **in the project folder**, because a `.conanrc` there can point to a project-specific home.
  `conan profile list` and `conan profile show -pr <name>` are safe to run.
- To add a profile, create a file in that folder, or run `conan profile detect --name <name>`.
  Then select it with `Conan.SelectProfiles`, which checks the names against
  `conan profile list`, and install.
- Editing a selected profile (or a profile it includes) makes the install out of date, so install
  again afterwards.
- Changing which profiles are selected changes which `conan-*` configurations Visual Studio
  offers. Ask the user before dropping a profile they selected.

## Files that belong to VsConan and Conan

Don't edit these by hand. VsConan rewrites them or relies on them:

- `CMakeUserPresets.json`, when it has a `vendor.conan` key. A hand-written one is never
  overwritten, and the hints say so. It may be the user's: ask before removing it.
- The `build/<profile>/...` folders Conan generates.
- `.vs/VsConan/` (`settings.json` holds the user's chosen profiles; `state.json`; results).
- `"enableCMake"` in `.vs/CMakeWorkspaceSettings.json`. VsConan turns it off while installing
  and back on afterwards. It leaves it off if someone else turned it off.

## When something's wrong

| Symptom | What to do |
| --- | --- |
| The command isn't available, or not found | VsConan may still be loading (wait a few seconds and retry), a dialog may be open in Visual Studio (ask the user to close it), or the folder isn't one VsConan manages. If the command doesn't exist, VsConan isn't installed: install dependencies with Conan the way the project documents. |
| `cmakePaused` and no profiles selected | Select profiles, then install. |
| A profile failed (`partiallyFailed`) | Read its `errorOutput`, fix the cause in the conanfile or profile, and install again. |
| `Invalid setting` / `compiler.version` errors | The profile doesn't match an installed toolset or a valid setting. Fix the profile (ask the user if it's theirs), then install. |
| `Could not run 'conan'` | Conan 2 isn't on the PATH Visual Studio started with. Tell the user. |
| No `conan-*` configurations after a successful install | Check the hints: usually the conanfile is missing `cmake_layout` or `CMakeToolchain`. |

## Visual Studio nobody is watching

If you start your own Visual Studio for a copy of the project, such as a git worktree:

- Set `VSCONAN_UNATTENDED=1` in its environment. VsConan then shows no windows, reinstalls as
  soon as the Conan inputs change, and keeps CMake paused until profiles are selected.
- Before opening the copy, copy `.vs/VsConan/settings.json` from the user's checkout, so the copy
  gets the same profiles.
- Also before opening it, start it paused: write `.vs/CMakeWorkspaceSettings.json` as
  `{ "enableCMake": false }` and `.vs/VsConan/state.json` as `{ "cmakeGateClosed": true }`.
  Otherwise Visual Studio configures the copy without the Conan toolchain before VsConan loads.

For the full contract, see VsConan's README: https://github.com/snargit/vsconan#automation
