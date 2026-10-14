using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VsConan.Core;

/// <summary>What Conan reports for the project folder, for the fingerprint.</summary>
public sealed class ResolvedConanInputs
{
    public ResolvedConanInputs(string home, IReadOnlyList<string> profiles)
    {
        Home = home;
        Profiles = profiles;
    }

    /// <summary><c>conan config home</c> (a .conanrc in the project folder or a parent, or CONAN_HOME).</summary>
    public string Home { get; }

    /// <summary><c>conan profile show</c> output for each host profile, in order.</summary>
    public IReadOnlyList<string> Profiles { get; }
}

/// <summary>
/// A hash of everything that decides what <c>conan install</c> produces, so the install can be
/// skipped when nothing has changed:
/// the conanfile, conan.lock and conandata.yml; the Conan home in use (so switching it counts);
/// and the selected profiles as Conan resolves them (<c>conan profile show</c>), so edits to a
/// profile or to a profile it includes count, and edits to unrelated profiles don't.
/// </summary>
public static class InstallFingerprint
{
    // Bump when the install arguments or the fingerprint's inputs change, to force a reinstall.
    private const string FormatVersion = "2";

    /// <summary>Files in the project folder that decide what <c>conan install</c> produces.</summary>
    public static readonly IReadOnlyList<string> InputFileNames = new[] { "conanfile.py", "conanfile.txt", "conan.lock", "conandata.yml" };

    public static string Compute(ConanProject project, VsConanSettings settings, ResolvedConanInputs resolved)
    {
        using var sha = SHA256.Create();
        using var stream = new MemoryStream();

        void Add(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }

        void AddFile(string path)
        {
            Add(Path.GetFileName(path));
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                stream.Write(bytes, 0, bytes.Length);
            }

            Add(string.Empty);
        }

        Add(FormatVersion);
        AddFile(project.ConanfilePath);
        foreach (var name in InputFileNames.Where(name => !name.StartsWith("conanfile.", StringComparison.Ordinal)))
        {
            AddFile(Path.Combine(project.RootDirectory, name));
        }

        Add("home:" + resolved.Home);
        Add("build:" + settings.BuildProfile);
        for (var i = 0; i < settings.HostProfiles.Count; i++)
        {
            Add("host:" + settings.HostProfiles[i]);
            Add(i < resolved.Profiles.Count ? resolved.Profiles[i] : string.Empty);
        }

        return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", string.Empty);
    }
}
