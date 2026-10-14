using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VsConan.Core;

/// <summary>
/// Pauses and resumes VS's CMake integration for a folder through the "enableCMake" setting in
/// .vs/CMakeWorkspaceSettings.json, which VS watches. While the gate is closed VS ignores the
/// presets and toolchain files Conan is rewriting, so it configures once afterwards instead of
/// once per file Conan touches.
/// </summary>
public sealed class CMakeGate
{
    private const string EnableCMake = "enableCMake";

    private readonly string _projectDirectory;

    public CMakeGate(string projectDirectory)
    {
        _projectDirectory = projectDirectory;
    }

    public static string GetPath(string projectDirectory) =>
        Path.Combine(projectDirectory, ".vs", "CMakeWorkspaceSettings.json");

    public bool IsCMakeEnabled
    {
        get
        {
            var value = ReadSettings()[EnableCMake];
            return value?.Type != JTokenType.Boolean || value.Value<bool>();
        }
    }

    /// <summary>
    /// Turns the CMake integration off. Does nothing if it's already off; if the user turned it
    /// off, <see cref="Open"/> won't turn it back on.
    /// </summary>
    public void Close()
    {
        if (!IsCMakeEnabled)
        {
            return;
        }

        // Record ownership before disabling: a stale marker is harmless, an unowned disable isn't.
        var state = VsConanState.Load(_projectDirectory);
        state.CMakeGateClosed = true;
        state.Save(_projectDirectory);

        WriteEnableCMake(false);
    }

    /// <summary>Turns the CMake integration back on, if <see cref="Close"/> turned it off.</summary>
    public void Open()
    {
        var state = VsConanState.Load(_projectDirectory);
        if (!state.CMakeGateClosed)
        {
            return;
        }

        WriteEnableCMake(true);

        state.CMakeGateClosed = false;
        state.Save(_projectDirectory);
    }

    private JObject ReadSettings()
    {
        var path = GetPath(_projectDirectory);
        return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
    }

    private void WriteEnableCMake(bool value)
    {
        // Keep any other settings (e.g. sourceDirectory) the user has in the file.
        var settings = ReadSettings();
        settings[EnableCMake] = value;
        AtomicFile.WriteAllTextIfChanged(GetPath(_projectDirectory), settings.ToString(Formatting.Indented));
    }
}
