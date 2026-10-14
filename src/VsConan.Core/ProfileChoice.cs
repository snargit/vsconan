using System;
using System.Collections.Generic;
using System.Linq;

namespace VsConan.Core;

public static class ProfileChoice
{
    /// <summary>
    /// Checks a profile choice made without the dialog (by automation) against the profiles Conan
    /// knows about.
    /// </summary>
    /// <param name="hosts">The host profiles with blanks and duplicates removed.</param>
    /// <returns>An error message, or null if the choice is valid.</returns>
    public static string? Validate(
        IReadOnlyList<string> available, IEnumerable<string> hostProfiles, string buildProfile, out List<string> hosts)
    {
        hosts = hostProfiles
            .Select(profile => profile.Trim())
            .Where(profile => profile.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (hosts.Count == 0)
        {
            return "Choose at least one host profile.";
        }

        var unknown = hosts.Append(buildProfile).Where(profile => !available.Contains(profile)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            var known = available.Count > 0 ? string.Join(", ", available) : "(none; create one with conan profile detect)";
            return $"Unknown Conan profile(s): {string.Join(", ", unknown)}. Available profiles: {known}.";
        }

        return null;
    }
}
