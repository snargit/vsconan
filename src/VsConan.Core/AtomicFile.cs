using System.IO;
using System.Text;

namespace VsConan.Core;

internal static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="content"/> unless the file already holds exactly that text, replacing the
    /// file in a single step. VS watches the files written here, so a partial write or an identical
    /// rewrite would each trigger a pointless reload.
    /// </summary>
    /// <returns>True if the file was written.</returns>
    public static bool WriteAllTextIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".vsconan.tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, path);
        }

        return true;
    }
}
