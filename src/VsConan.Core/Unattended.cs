using System;

namespace VsConan.Core;

/// <summary>
/// Whether Visual Studio runs without anyone at it (e.g. a hidden instance an agent drives), from the
/// VSCONAN_UNATTENDED environment variable ("1" or "true"). Then VsConan shows no windows or info bars:
/// a folder without selected profiles stays paused until automation selects them, and changed Conan
/// inputs are reinstalled straight away.
/// </summary>
public static class Unattended
{
    public const string VariableName = "VSCONAN_UNATTENDED";

    public static bool IsSet(Func<string, string?> getVariable)
    {
        var value = getVariable(VariableName)?.Trim();
        return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSet() => IsSet(Environment.GetEnvironmentVariable);
}
