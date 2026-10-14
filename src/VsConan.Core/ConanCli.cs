using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VsConan.Core;

/// <summary>
/// Thin wrapper over the Conan 2 command line.
/// </summary>
public sealed class ConanCli
{
    public ConanCli(string executable = "conan")
    {
        Executable = executable;
    }

    public string Executable { get; }

    public async Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            Executable, new[] { "profile", "list", "--format=json" }, null, null, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"'conan profile list' failed with exit code {result.ExitCode}.");
        }

        return ParseProfileList(result.StandardOutput);
    }

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
