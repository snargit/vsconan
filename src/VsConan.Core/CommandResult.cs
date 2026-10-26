using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace VsConan.Core;

/// <summary>
/// The outcome of the last VsConan command run by automation, in .vs/VsConan/command-result.json.
/// VS commands can't return values and the work runs asynchronously, so callers read this file:
/// "running" while the command runs, then "succeeded" or "failed".
/// </summary>
public sealed class CommandResult
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// <summary>The caller's <c>/id:</c> token, if it passed one.</summary>
    [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)]
    public string? Id { get; set; }

    [JsonProperty("command")]
    public string Command { get; set; } = string.Empty;

    [JsonProperty("state")]
    public string State { get; set; } = Running;

    [JsonProperty("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonProperty("completedAt", NullValueHandling = NullValueHandling.Ignore)]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Why the command failed, worded so the caller can correct the call.</summary>
    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    public string? Error { get; set; }

    /// <summary>A <see cref="ProjectStatus"/> or <see cref="InstallReport"/>, depending on the command.</summary>
    [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
    public object? Result { get; set; }

    /// <summary>Results older than this are deleted when a new one is written.</summary>
    private static readonly TimeSpan KeepResultsFor = TimeSpan.FromDays(1);

    public static string GetPath(string projectDirectory) =>
        Path.Combine(projectDirectory, ".vs", "VsConan", "command-result.json");

    /// <summary>
    /// Where the result of the request with <c>/id:</c><paramref name="id"/> is kept, so callers running
    /// commands at the same time each find theirs; null for ids that aren't safe as file names
    /// (letters, digits, '-' and '_', up to 64 characters).
    /// </summary>
    public static string? GetPath(string projectDirectory, string? id) =>
        IsFileSafe(id) ? Path.Combine(projectDirectory, ".vs", "VsConan", "command-results", id + ".json") : null;

    private static bool IsFileSafe(string? id) =>
        !string.IsNullOrEmpty(id) && id!.Length <= 64 && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>Writes command-result.json (the most recent command) and, for a request with an id, its own file.</summary>
    public void Save(string projectDirectory)
    {
        var json = JsonConvert.SerializeObject(this, Formatting.Indented);
        AtomicFile.WriteAllTextIfChanged(GetPath(projectDirectory), json);
        if (GetPath(projectDirectory, Id) is string own)
        {
            if (State == Running)
            {
                DeleteOldResults(Path.GetDirectoryName(own)!);
            }

            AtomicFile.WriteAllTextIfChanged(own, json);
        }
    }

    private static void DeleteOldResults(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > KeepResultsFor)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // In use or already gone: it goes next time.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>What a caller needs to decide what to do next with the open folder.</summary>
public sealed class ProjectStatus
{
    [JsonProperty("folder")]
    public string Folder { get; set; } = string.Empty;

    [JsonProperty("conanfile")]
    public string Conanfile { get; set; } = string.Empty;

    [JsonProperty("usesCmakeLayout")]
    public bool UsesCmakeLayout { get; set; }

    /// <summary>Host profiles selected for install; empty until profiles are chosen.</summary>
    [JsonProperty("hostProfiles")]
    public List<string> HostProfiles { get; set; } = new();

    [JsonProperty("buildProfile")]
    public string BuildProfile { get; set; } = string.Empty;

    /// <summary>True if the last install still matches the conanfile, conan.lock and profiles.</summary>
    [JsonProperty("upToDate")]
    public bool UpToDate { get; set; }

    /// <summary>True while a Conan install is running for this folder (CMake is paused until it finishes).</summary>
    [JsonProperty("installing")]
    public bool Installing { get; set; }

    /// <summary>True while VS's CMake integration is turned off for this folder (by VsConan or the user).</summary>
    [JsonProperty("cmakePaused")]
    public bool CMakePaused { get; set; }

    /// <summary>conan-* configure presets currently available to VS.</summary>
    [JsonProperty("configurePresets")]
    public List<string> ConfigurePresets { get; set; } = new();

    /// <summary>The output of 'conan profile list', when it was requested.</summary>
    [JsonProperty("availableProfiles", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? AvailableProfiles { get; set; }

    /// <param name="cli">Conan, run in the project folder; used to check whether the install is up to date.</param>
    public static async Task<ProjectStatus> CreateAsync(
        ConanProject project, ConanCli cli, IReadOnlyList<string>? availableProfiles, CancellationToken cancellationToken,
        bool installing = false)
    {
        var directory = project.RootDirectory;
        var settings = VsConanSettings.Load(directory);
        var upToDate = settings.HostProfiles.Count > 0
            && await DependencySync.IsUpToDateAsync(project, settings, cli, cancellationToken).ConfigureAwait(false);
        return new ProjectStatus
        {
            Folder = directory,
            Conanfile = Path.GetFileName(project.ConanfilePath),
            UsesCmakeLayout = project.UsesCmakeLayout,
            HostProfiles = settings.HostProfiles.ToList(),
            BuildProfile = settings.BuildProfile,
            UpToDate = upToDate,
            Installing = installing,
            CMakePaused = !new CMakeGate(directory).IsCMakeEnabled,
            ConfigurePresets = UserPresetsFile.CurrentConfigurePresetNames(directory).ToList(),
            AvailableProfiles = availableProfiles?.ToList(),
        };
    }
}

/// <summary>The outcome of an install run by automation.</summary>
public sealed class InstallReport
{
    /// <summary>upToDate, installed, partiallyFailed or userPresetsNotOwned.</summary>
    [JsonProperty("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonProperty("profiles")]
    public List<ProfileOutcome> Profiles { get; set; } = new();

    /// <summary>conan-* configure presets now available to VS.</summary>
    [JsonProperty("configurePresets")]
    public List<string> ConfigurePresets { get; set; } = new();

    public static InstallReport Create(ConanProject project, SyncResult result) => new()
    {
        Outcome = result.Outcome switch
        {
            SyncOutcome.UpToDate => "upToDate",
            SyncOutcome.Installed => "installed",
            SyncOutcome.PartiallyFailed => "partiallyFailed",
            _ => "userPresetsNotOwned",
        },
        Profiles = result.Profiles
            .Select(p => new ProfileOutcome { Profile = p.Profile, Succeeded = p.Succeeded, ExitCode = p.ExitCode })
            .ToList(),
        ConfigurePresets = UserPresetsFile.CurrentConfigurePresetNames(project.RootDirectory).ToList(),
    };

    public sealed class ProfileOutcome
    {
        [JsonProperty("profile")]
        public string Profile { get; set; } = string.Empty;

        [JsonProperty("succeeded")]
        public bool Succeeded { get; set; }

        [JsonProperty("exitCode")]
        public int ExitCode { get; set; }
    }
}
