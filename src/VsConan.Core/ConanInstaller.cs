using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VsConan.Core;

public sealed class ProfileInstallResult
{
    public ProfileInstallResult(string profile, int exitCode, string? presetsFile)
    {
        Profile = profile;
        ExitCode = exitCode;
        PresetsFile = presetsFile;
    }

    public string Profile { get; }

    public int ExitCode { get; }

    /// <summary>The CMakePresets.json Conan generated for this profile, if the install succeeded.</summary>
    public string? PresetsFile { get; }

    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs <c>conan install</c> for each selected host profile. Each install writes the profile's
/// own CMakePresets.json; <see cref="DependencySync"/> then lists them in CMakeUserPresets.json.
/// </summary>
public sealed class ConanInstaller
{
    private readonly ConanCli _cli;

    public ConanInstaller(ConanCli cli)
    {
        _cli = cli;
    }

    public async Task<IReadOnlyList<ProfileInstallResult>> InstallAsync(
        ConanProject project,
        VsConanSettings settings,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        var results = new List<ProfileInstallResult>();

        // Sequential on purpose: concurrent installs into the same Conan cache aren't safe.
        foreach (var profile in settings.HostProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onOutput($"========== conan install: profile '{profile}' ==========");

            var request = new ConanInstallRequest(project.RootDirectory, profile, settings.BuildProfile);
            results.Add(await _cli.InstallAsync(request, onOutput, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }
}
