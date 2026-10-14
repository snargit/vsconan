using System;
using System.IO;
using VsConan.Core;

namespace VsConan.Core.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vsconan-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    /// <summary>Creates a file relative to the directory and returns its full path.</summary>
    public string Write(string name, string content = "")
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class ConanInstallRequestTests
{
    [Fact]
    public void BuildArguments_NamesPresetsAfterProfile()
    {
        var args = new ConanInstallRequest(@"C:\src\app", "msvc-debug", "default").BuildArguments();

        Assert.Equal(
            new[]
            {
                "install", @"C:\src\app",
                "-pr:h", "msvc-debug",
                "-pr:b", "default",
                "-c", "tools.cmake.cmake_layout:build_folder_vars=['const.msvc-debug']",
                "-c", "tools.cmake.cmaketoolchain:generator=Ninja",
                "-c", "tools.cmake.cmaketoolchain:user_presets=",
                "--format=json",
                "--build=missing",
            },
            args);
    }

    [Fact]
    public void BuildArguments_OmitsBuildMissingWhenDisabled()
    {
        var request = new ConanInstallRequest("dir", "p", "default") { BuildMissing = false };

        Assert.DoesNotContain("--build=missing", request.BuildArguments());
    }
}

public class ProcessRunnerTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "\"\"")]
    [InlineData(@"C:\Program Files\x", "\"C:\\Program Files\\x\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData(@"no\space\", @"no\space\")]
    public void QuoteArgument_FollowsWindowsRules(string input, string expected)
    {
        Assert.Equal(expected, ProcessRunner.QuoteArgument(input));
    }
}

public class ConanCliTests
{
    [Fact]
    public void ParseProfileList_ReadsJsonArray()
    {
        var profiles = ConanCli.ParseProfileList("[\n    \"debug\",\n    \"default\"\n]\n");

        Assert.Equal(new[] { "debug", "default" }, profiles);
    }

    [Fact]
    public void ParseGeneratorsFolder_ReadsConsumerNode()
    {
        const string json = """
            { "graph": { "nodes": {
                "0": { "ref": "conanfile", "generators_folder": "C:\\src\\app\\build\\debug\\Debug\\generators" },
                "1": { "ref": "zlib/1.3", "generators_folder": null } } } }
            """;

        Assert.Equal(@"C:\src\app\build\debug\Debug\generators", ConanCli.ParseGeneratorsFolder(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"graph\": {} }")]
    public void ParseGeneratorsFolder_ReturnsNullForUnexpectedOutput(string json)
    {
        Assert.Null(ConanCli.ParseGeneratorsFolder(json));
    }
}

public class CMakeGateTests
{
    [Fact]
    public void CloseThenOpen_TogglesEnableCMakeAndKeepsOtherSettings()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, ".vs"));
        File.WriteAllText(CMakeGate.GetPath(dir.Path), "{ \"sourceDirectory\": \"src\" }");
        var gate = new CMakeGate(dir.Path);

        gate.Close();
        Assert.False(gate.IsCMakeEnabled);
        Assert.True(VsConanState.Load(dir.Path).CMakeGateClosed);

        gate.Open();
        Assert.True(gate.IsCMakeEnabled);
        Assert.False(VsConanState.Load(dir.Path).CMakeGateClosed);
        Assert.Contains("\"sourceDirectory\": \"src\"", File.ReadAllText(CMakeGate.GetPath(dir.Path)));
    }

    [Fact]
    public void Open_LeavesCMakeDisabledWhenUserDisabledIt()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, ".vs"));
        File.WriteAllText(CMakeGate.GetPath(dir.Path), "{ \"enableCMake\": false }");
        var gate = new CMakeGate(dir.Path);

        gate.Close();
        gate.Open();

        Assert.False(gate.IsCMakeEnabled);
    }

    [Fact]
    public void Open_WithoutClose_DoesNotCreateSettingsFile()
    {
        using var dir = new TempDirectory();

        new CMakeGate(dir.Path).Open();

        Assert.False(File.Exists(CMakeGate.GetPath(dir.Path)));
    }
}

public class UserPresetsFileTests
{
    [Fact]
    public void Write_IncludesRelativePathsAndRoundTrips()
    {
        using var dir = new TempDirectory();
        var presets = Path.Combine(dir.Path, "build", "debug", "Debug", "generators", "CMakePresets.json");

        Assert.True(UserPresetsFile.Write(dir.Path, new[] { presets }));

        Assert.Contains("\"build/debug/Debug/generators/CMakePresets.json\"", File.ReadAllText(UserPresetsFile.GetPath(dir.Path)));
        Assert.Equal(new[] { presets }, UserPresetsFile.ReadIncludes(dir.Path));
        Assert.True(UserPresetsFile.CanWrite(dir.Path));
    }

    [Fact]
    public void Write_SkipsIdenticalContent()
    {
        using var dir = new TempDirectory();
        var presets = new[] { Path.Combine(dir.Path, "a", "CMakePresets.json") };
        UserPresetsFile.Write(dir.Path, presets);

        Assert.False(UserPresetsFile.Write(dir.Path, presets));
    }

    [Fact]
    public void CanWrite_IsFalseForHandWrittenFile()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(UserPresetsFile.GetPath(dir.Path), "{ \"version\": 4, \"configurePresets\": [] }");

        Assert.False(UserPresetsFile.CanWrite(dir.Path));
    }
}

public class InstallFingerprintTests
{
    [Fact]
    public void Compute_ChangesWithConanfileAndProfiles()
    {
        using var dir = new TempDirectory();
        dir.Write("CMakeLists.txt");
        var conanfile = dir.Write("conanfile.txt", "[requires]\nzlib/1.3\n");
        var settings = new VsConanSettings { HostProfiles = { "default" } };

        var original = InstallFingerprint.Compute(ConanProject.Find(dir.Path)!, settings);
        Assert.Equal(original, InstallFingerprint.Compute(ConanProject.Find(dir.Path)!, settings));

        settings.HostProfiles.Add("debug");
        Assert.NotEqual(original, InstallFingerprint.Compute(ConanProject.Find(dir.Path)!, settings));

        settings.HostProfiles.Remove("debug");
        File.WriteAllText(conanfile, "[requires]\nzlib/1.3.1\n");
        Assert.NotEqual(original, InstallFingerprint.Compute(ConanProject.Find(dir.Path)!, settings));
    }
}

public class ConanProjectTests
{
    [Theory]
    [InlineData]                                            // empty folder
    [InlineData("CMakeLists.txt")]                          // plain CMake project
    [InlineData("conanfile.py")]                            // Conan, but not CMake
    [InlineData("conanfile.txt")]
    [InlineData("CMakeLists.txt", "conan.lock")]            // Conan artifacts that can't be installed
    [InlineData("CMakeLists.txt", "conandata.yml")]
    [InlineData("CMakeLists.txt", "src/conanfile.py")]      // conanfile not at the root
    [InlineData("conanfile.py", "src/CMakeLists.txt")]      // CMakeLists.txt not at the root
    public void Find_IgnoresFoldersThatArentConanCMakeProjects(params string[] files)
    {
        using var dir = new TempDirectory();
        foreach (var file in files)
        {
            dir.Write(file);
        }

        Assert.Null(ConanProject.Find(dir.Path));
        Assert.False(ConanProject.IsConanCMakeProject(dir.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"Z:\does\not\exist")]
    public void Find_IgnoresMissingFolder(string? directory)
    {
        Assert.Null(ConanProject.Find(directory));
        Assert.False(ConanProject.IsConanCMakeProject(directory));
    }

    [Theory]
    [InlineData("conanfile.py")]
    [InlineData("conanfile.txt")]
    public void Find_AcceptsConanfileWithCMakeLists(string conanfile)
    {
        using var dir = new TempDirectory();
        dir.Write("CMakeLists.txt");
        dir.Write(conanfile);

        Assert.True(ConanProject.IsConanCMakeProject(dir.Path));
        Assert.EndsWith(conanfile, ConanProject.Find(dir.Path)!.ConanfilePath);
    }

    [Fact]
    public void Find_PrefersConanfilePy()
    {
        using var dir = new TempDirectory();
        dir.Write("CMakeLists.txt");
        dir.Write("conanfile.txt", "[layout]\ncmake_layout\n");
        dir.Write("conanfile.py", "class App(ConanFile): pass\n");

        var project = ConanProject.Find(dir.Path);

        Assert.NotNull(project);
        Assert.EndsWith("conanfile.py", project!.ConanfilePath);
        Assert.False(project.UsesCmakeLayout);
    }

    [Fact]
    public void Find_DetectsCmakeLayout()
    {
        using var dir = new TempDirectory();
        dir.Write("CMakeLists.txt");
        dir.Write("conanfile.txt", "[generators]\nCMakeToolchain\n[layout]\ncmake_layout\n");

        Assert.True(ConanProject.Find(dir.Path)!.UsesCmakeLayout);
    }
}

public class VsConanSettingsTests
{
    [Fact]
    public void Load_ReturnsDefaultsWhenMissing()
    {
        using var dir = new TempDirectory();

        var settings = VsConanSettings.Load(dir.Path);

        Assert.Empty(settings.HostProfiles);
        Assert.Equal("default", settings.BuildProfile);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var dir = new TempDirectory();
        new VsConanSettings { HostProfiles = { "default", "debug" }, BuildProfile = "build" }.Save(dir.Path);

        var loaded = VsConanSettings.Load(dir.Path);

        Assert.Equal(new[] { "default", "debug" }, loaded.HostProfiles);
        Assert.Equal("build", loaded.BuildProfile);
        Assert.True(File.Exists(Path.Combine(dir.Path, ".vs", "VsConan", "settings.json")));
    }
}
