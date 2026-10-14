# Samples

A minimal setup for trying VsConan by hand. It's fully local and needs no Conan remote.

| Folder | Role |
| --- | --- |
| `greeting/` | Upstream dependency: a small static library packaged as a Conan recipe (`greeting/0.1`). |
| `hello-app/` | The project to open in Visual Studio: `conanfile.txt` (requires `greeting/0.1`, uses `cmake_layout`) and a `CMakeLists.txt`. |

## Try it

1. Put the `greeting` recipe in your local Conan cache. This only exports the recipe; nothing is
   built yet:

   ```sh
   conan export samples/greeting
   ```

2. In the Visual Studio experimental instance (press F5 on `VsConan.sln`), open `samples/hello-app`
   with **File > Open > Folder**.
3. VsConan pauses CMake and opens a window that asks which Conan profiles to install. Pick one or
   more host profiles and click **Install**. This window only appears on the first open; after
   that, use **Tools > Conan > Select Profiles...**.
4. VsConan runs `conan install` for each profile. `--build=missing` builds `greeting` from source
   for each profile. VsConan then writes `CMakeUserPresets.json` and resumes CMake.
5. When the info bar appears, click **Switch to CMake Targets View** to see `hello-app` as a CMake
   target in Solution Explorer. The link works once CMake has finished configuring.
6. Pick a `conan-<profile>-<build type>` configuration from the toolbar, then build and run
   `hello-app.exe`. It prints the build type of the `greeting` it linked against, e.g.
   `Hello, VsConan! (greeting Debug build)`.

## Things to check

- While the install runs, the CMake output shouldn't show any configure activity. Once CMake
  resumes, it should configure exactly once.
- Close and reopen the folder. Nothing has changed, so VsConan should skip the install and
  resume CMake straight away.
- Edit `conanfile.txt` (e.g. add a blank line), close the folder, then reopen it. VsConan should
  reinstall before CMake configures.
- Open `samples/greeting` on its own. It has a conanfile and a `CMakeLists.txt`, so VsConan
  manages it too. Open `samples` itself to check that VsConan does nothing there: the folder has
  neither file at its root.

## Resetting

To start over, remove the generated files from `hello-app` (`build/`, `CMakeUserPresets.json`, and
`.vs/` for VsConan's state and the CMake pause). Deleting `.vs/VsConan/` alone is enough to see the
first-run window again. To remove the package from the cache, run
`conan remove "greeting/*" -c`.
