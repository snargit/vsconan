using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace VsConan.Core;

/// <summary>
/// What a CMakeLists.txt needs to use a profile's direct dependencies: the names for find_package()
/// and the targets for target_link_libraries(). They come from the summary CMakeDeps (and
/// CMakeConfigDeps) print during an install, so they are the names the recipes really define,
/// which often differ from the package names.
/// </summary>
public sealed class CMakeUsage
{
    [JsonProperty("findPackages")]
    public List<string> FindPackages { get; set; } = new();

    [JsonProperty("targets")]
    public List<string> Targets { get; set; } = new();
}

/// <summary>
/// Picks the CMakeDeps summary out of <c>conan install</c>'s log as it streams:
/// <code>
/// CMakeDeps necessary find_package() and targets for your CMakeLists.txt
///     find_package(fmt)
///     find_package(protobuf) # Optional. This is a tool-require, can't link its targets
///     target_link_libraries(... fmt::fmt)
/// </code>
/// The header may follow a prefix such as <c>conanfile.py (app/1.0): </c>.
/// </summary>
internal sealed class CMakeUsageCollector
{
    private const string Header = "CMakeDeps necessary find_package() and targets for your CMakeLists.txt";
    private const string FindPackage = "find_package(";
    private const string TargetLinkLibraries = "target_link_libraries(";

    private static readonly Regex Escapes = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    private readonly List<string> _packages = new();
    private readonly List<string> _targets = new();
    private bool _inSummary;

    /// <summary>Reads one line of the log. Safe to call from the output threads.</summary>
    public void Add(string line)
    {
        lock (_packages)
        {
            line = Escapes.Replace(line, string.Empty);
            if (line.Contains(Header))
            {
                _inSummary = true;
                return;
            }

            if (!_inSummary)
            {
                return;
            }

            var text = line.Trim();
            if (text.StartsWith(FindPackage) && text.IndexOf(')') is var close and > 0)
            {
                AddDistinct(_packages, new[] { text.Substring(FindPackage.Length, close - FindPackage.Length).Trim() });
            }
            else if (text.StartsWith(TargetLinkLibraries) && text.LastIndexOf(')') is var end and > 0)
            {
                var targets = text.Substring(TargetLinkLibraries.Length, end - TargetLinkLibraries.Length)
                    .Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t != "...");
                AddDistinct(_targets, targets);
            }
            else
            {
                _inSummary = false;
            }
        }
    }

    /// <summary>The names collected, or null if the log had no CMakeDeps summary.</summary>
    public CMakeUsage? Result
    {
        get
        {
            lock (_packages)
            {
                return _packages.Count == 0 && _targets.Count == 0
                    ? null
                    : new CMakeUsage { FindPackages = _packages.ToList(), Targets = _targets.ToList() };
            }
        }
    }

    private static void AddDistinct(List<string> list, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (name.Length > 0 && !list.Contains(name))
            {
                list.Add(name);
            }
        }
    }
}
