using System.IO;
using System.Linq;

namespace VsConan.Core;

/// <summary>
/// A folder VsConan manages: a CMake project whose dependencies come from Conan. Any other folder
/// is left alone.
/// </summary>
public sealed class ConanProject
{
    // The only files 'conan install <folder>' accepts. Other Conan artifacts (conan.lock,
    // conandata.yml) can't be installed on their own, so they don't make a folder a Conan project.
    private static readonly string[] ConanfileNames = { "conanfile.py", "conanfile.txt" };

    private const string CMakeListsName = "CMakeLists.txt";

    private ConanProject(string rootDirectory, string conanfilePath, bool usesCmakeLayout)
    {
        RootDirectory = rootDirectory;
        ConanfilePath = conanfilePath;
        UsesCmakeLayout = usesCmakeLayout;
    }

    public string RootDirectory { get; }

    public string ConanfilePath { get; }

    /// <summary>
    /// True when the conanfile appears to use cmake_layout. Without it, Conan names presets by
    /// build type only, so installing several profiles produces colliding preset names.
    /// </summary>
    public bool UsesCmakeLayout { get; }

    /// <summary>
    /// Cheap check (file existence only) that <paramref name="directory"/> has both a conanfile and a
    /// CMakeLists.txt at its root. Suitable for frequent calls such as command status queries.
    /// </summary>
    public static bool IsConanCMakeProject(string? directory) => FindConanfile(directory) is not null;

    /// <summary>
    /// Returns the Conan project rooted at <paramref name="directory"/>, or null unless it has both a
    /// conanfile and a CMakeLists.txt. conanfile.py takes precedence over conanfile.txt, as in Conan.
    /// </summary>
    public static ConanProject? Find(string? directory)
    {
        var conanfile = FindConanfile(directory);
        if (conanfile is null)
        {
            return null;
        }

        var usesCmakeLayout = File.ReadAllText(conanfile).Contains("cmake_layout");
        return new ConanProject(directory!, conanfile, usesCmakeLayout);
    }

    private static string? FindConanfile(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !File.Exists(Path.Combine(directory, CMakeListsName)))
        {
            return null;
        }

        return ConanfileNames.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
    }
}
