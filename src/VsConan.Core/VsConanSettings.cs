using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace VsConan.Core;

/// <summary>
/// Per-user, per-project settings, stored under the project's .vs folder.
/// </summary>
public sealed class VsConanSettings
{
    [JsonProperty("hostProfiles")]
    public List<string> HostProfiles { get; set; } = new();

    [JsonProperty("buildProfile")]
    public string BuildProfile { get; set; } = "default";

    public static string GetPath(string projectDirectory) =>
        Path.Combine(projectDirectory, ".vs", "VsConan", "settings.json");

    /// <summary>Loads the settings, or returns defaults if none have been saved.</summary>
    public static VsConanSettings Load(string projectDirectory)
    {
        var path = GetPath(projectDirectory);
        if (!File.Exists(path))
        {
            return new VsConanSettings();
        }

        return JsonConvert.DeserializeObject<VsConanSettings>(File.ReadAllText(path)) ?? new VsConanSettings();
    }

    public void Save(string projectDirectory)
    {
        var path = GetPath(projectDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}
