using System.Collections.Generic;

namespace VsConan.Core;

/// <summary>
/// Arguments for a single <c>conan install</c> of one host profile.
/// </summary>
public sealed class ConanInstallRequest
{
    public ConanInstallRequest(string projectDirectory, string hostProfile, string buildProfile)
    {
        ProjectDirectory = projectDirectory;
        HostProfile = hostProfile;
        BuildProfile = buildProfile;
    }

    public string ProjectDirectory { get; }

    public string HostProfile { get; }

    public string BuildProfile { get; }

    /// <summary>CMake generator written into the presets. Ninja is what VS's CMake integration expects.</summary>
    public string Generator { get; set; } = "Ninja";

    public bool BuildMissing { get; set; } = true;

    public IReadOnlyList<string> BuildArguments()
    {
        var args = new List<string>
        {
            "install", ProjectDirectory,
            "-pr:h", HostProfile,
            "-pr:b", BuildProfile,
            // Put the profile name into cmake_layout's build folder and preset names
            // (e.g. build/<profile>/Release, conan-<profile>-release) so profiles don't collide.
            "-c", $"tools.cmake.cmake_layout:build_folder_vars=['const.{HostProfile}']",
            "-c", $"tools.cmake.cmaketoolchain:generator={Generator}",
            // VsConan writes CMakeUserPresets.json once after all profiles, so VS reloads once.
            "-c", "tools.cmake.cmaketoolchain:user_presets=",
            // The JSON graph on stdout reports where the presets were generated.
            "--format=json",
        };

        if (BuildMissing)
        {
            args.Add("--build=missing");
        }

        return args;
    }
}
