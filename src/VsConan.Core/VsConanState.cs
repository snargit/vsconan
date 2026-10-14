using System.IO;
using Newtonsoft.Json;

namespace VsConan.Core;

/// <summary>
/// Bookkeeping the extension keeps for a project, in .vs/VsConan/state.json. Unlike
/// <see cref="VsConanSettings"/> this is never edited by the user.
/// </summary>
public sealed class VsConanState
{
    /// <summary>True while VsConan (rather than the user) has VS's CMake integration turned off.</summary>
    [JsonProperty("cmakeGateClosed")]
    public bool CMakeGateClosed { get; set; }

    /// <summary>
    /// True once the first-run profile window has been shown for this folder, whatever the user
    /// chose. After that, profile changes go through the Tools > Conan menu.
    /// </summary>
    [JsonProperty("firstRunPromptShown")]
    public bool FirstRunPromptShown { get; set; }

    /// <summary>True once the "switch to CMake Targets View" info bar has been offered for this folder.</summary>
    [JsonProperty("targetsViewHintShown")]
    public bool TargetsViewHintShown { get; set; }

    /// <summary><see cref="InstallFingerprint"/> of the last fully successful install.</summary>
    [JsonProperty("installFingerprint")]
    public string? InstallFingerprint { get; set; }

    public static string GetPath(string projectDirectory) =>
        Path.Combine(projectDirectory, ".vs", "VsConan", "state.json");

    public static VsConanState Load(string projectDirectory)
    {
        var path = GetPath(projectDirectory);
        if (!File.Exists(path))
        {
            return new VsConanState();
        }

        return JsonConvert.DeserializeObject<VsConanState>(File.ReadAllText(path)) ?? new VsConanState();
    }

    public void Save(string projectDirectory)
    {
        AtomicFile.WriteAllTextIfChanged(GetPath(projectDirectory), JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}
