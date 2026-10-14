using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VsConan.Core;

/// <summary>
/// A hash of everything that decides what <c>conan install</c> produces, so opening a folder can
/// skip the install when nothing has changed. Edits to the Conan profiles themselves aren't
/// covered; run Install Dependencies after changing one.
/// </summary>
public static class InstallFingerprint
{
    // Bump when the install arguments change, to force a reinstall.
    private const string FormatVersion = "1";

    public static string Compute(ConanProject project, VsConanSettings settings)
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
        AddFile(Path.Combine(project.RootDirectory, "conan.lock"));
        Add("build:" + settings.BuildProfile);
        foreach (var profile in settings.HostProfiles)
        {
            Add("host:" + profile);
        }

        return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", string.Empty);
    }
}
