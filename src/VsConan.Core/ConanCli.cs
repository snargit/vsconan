using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VsConan.Core;

/// <summary>
/// Thin wrapper over the Conan 2 command line. Commands run in <see cref="WorkingDirectory"/> (the
/// project folder): Conan picks its home, and so its profiles, from a .conanrc in the working
/// directory or any parent before CONAN_HOME and the default ~/.conan2, so the project's own setup applies.
/// </summary>
public sealed class ConanCli
{
    public ConanCli(string executable = "conan", string? workingDirectory = null)
    {
        Executable = executable;
        WorkingDirectory = workingDirectory;
    }

    public string Executable { get; }

    public string? WorkingDirectory { get; }

    public async Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(new[] { "profile", "list", "--format=json" }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"'conan profile list' failed with exit code {result.ExitCode}.");
        }

        return ParseProfileList(result.StandardOutput);
    }

    /// <summary>
    /// The profiles as Conan resolves them for an install (includes, templates and defaults applied),
    /// as JSON text. Throws if a profile doesn't exist or can't be parsed.
    /// </summary>
    public async Task<string> ShowProfilesAsync(string hostProfile, string buildProfile, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            new[] { "profile", "show", "-pr:h", hostProfile, "-pr:b", buildProfile, "--format=json" }, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"'conan profile show' failed for profile '{hostProfile}' (exit code {result.ExitCode}).");
        }

        return result.StandardOutput.Trim();
    }

    /// <summary>Conan's home folder (<c>conan config home</c>), which holds the profiles, as seen from <see cref="WorkingDirectory"/>.</summary>
    public async Task<string> ConfigHomeAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(new[] { "config", "home" }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"'conan config home' failed with exit code {result.ExitCode}.");
        }

        return result.StandardOutput.Trim();
    }

    private Task<ProcessResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(Executable, arguments, WorkingDirectory, null, cancellationToken);

    /// <summary>Runs the install. Conan's log goes to <paramref name="onOutput"/>; the JSON graph is parsed.</summary>
    public async Task<ProfileInstallResult> InstallAsync(
        ConanInstallRequest request, Action<string> onOutput, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            Executable, request.BuildArguments(), request.ProjectDirectory, onOutput, cancellationToken,
            streamStandardOutput: false)
            .ConfigureAwait(false);

        string? presetsFile = null;
        if (result.ExitCode == 0)
        {
            var generatorsFolder = ParseGeneratorsFolder(result.StandardOutput);
            if (generatorsFolder is not null)
            {
                presetsFile = Path.Combine(generatorsFolder, "CMakePresets.json");
            }
        }

        return new ProfileInstallResult(request.HostProfile, result.ExitCode, presetsFile);
    }

    internal static IReadOnlyList<string> ParseProfileList(string json)
    {
        return JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
    }

    /// <summary>Reads the consumer's (node "0") generators folder from <c>conan install --format=json</c>.</summary>
    internal static string? ParseGeneratorsFolder(string json)
    {
        try
        {
            return (string?)JObject.Parse(json)["graph"]?["nodes"]?["0"]?["generators_folder"];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
