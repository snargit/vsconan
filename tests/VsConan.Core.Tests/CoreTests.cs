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
    private static ResolvedConanInputs Inputs(string home = "/conan-home", params string[] profiles) =>
        new(home, profiles.Length > 0 ? profiles : new[] { "{\"host\": {}}" });

    [Fact]
    public void Compute_ChangesWithProjectFiles()
    {
        using var dir = new TempDirectory();
        dir.Write("CMakeLists.txt");
        var conanfile = dir.Write("conanfile.txt", "[requires]\nzlib/1.3\n");
        var settings = new VsConanSettings { HostProfiles = { "default" } };
        string Fingerprint() => InstallFingerprint.Compute(ConanProject.Find(dir.Path)!, settings, Inputs());

        var original = Fingerprint();
        Assert.Equal(original, Fingerprint());

        File.WriteAllText(conanfile, "[requires]\nzlib/1.3.1\n");
        Assert.NotEqual(original, Fingerprint());

        foreach (var name in new[] { "conan.lock", "conandata.yml" })
        {
            var before = Fingerprint();
            dir.Write(name, "something");
            Assert.NotEqual(before, Fingerprint());
        }
    }

    [Fact]
    public void Compute_ChangesWithProfilesAndConanHome()
    {
        using var dir = new TempDirectory();
        dir.Write("CMakeLists.txt");
        dir.Write("conanfile.txt", "[requires]\nzlib/1.3\n");
        var project = ConanProject.Find(dir.Path)!;
        var settings = new VsConanSettings { HostProfiles = { "default" } };
        var original = InstallFingerprint.Compute(project, settings, Inputs());

        var twoHosts = new VsConanSettings { HostProfiles = { "default", "debug" } };
        Assert.NotEqual(original, InstallFingerprint.Compute(project, twoHosts, Inputs("/conan-home", "{}", "{}")));
        Assert.NotEqual(original, InstallFingerprint.Compute(project, new VsConanSettings { HostProfiles = { "default" }, BuildProfile = "debug" }, Inputs()));
        // The same profile name resolving differently (the profile, or one it includes, was edited).
        Assert.NotEqual(original, InstallFingerprint.Compute(project, settings, Inputs("/conan-home", "{\"host\": {\"x\": 1}}")));
        // A different Conan home (CONAN_HOME or a .conanrc).
        Assert.NotEqual(original, InstallFingerprint.Compute(project, settings, Inputs("/other-home")));
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

public class VsConanStateTests
{
    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var dir = new TempDirectory();
        Assert.False(VsConanState.Load(dir.Path).FirstRunPromptShown);

        new VsConanState { FirstRunPromptShown = true, TargetsViewHintShown = true, CMakeGateClosed = true, InstallFingerprint = "abc" }.Save(dir.Path);

        var loaded = VsConanState.Load(dir.Path);
        Assert.True(loaded.FirstRunPromptShown);
        Assert.True(loaded.TargetsViewHintShown);
        Assert.True(loaded.CMakeGateClosed);
        Assert.Equal("abc", loaded.InstallFingerprint);
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

public class AutomationArgumentsTests
{
    [Fact]
    public void Parse_SplitsProfilesAndSwitches()
    {
        var args = AutomationArguments.Parse("default, debug /build:gcc /FORCE /id:req-7");

        Assert.Equal(new[] { "default", "debug" }, args.HostProfiles);
        Assert.Equal("gcc", args.BuildProfile);
        Assert.True(args.Force);
        Assert.Equal("req-7", args.RequestId);
        Assert.Empty(args.Errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyHasNothing(string? text)
    {
        var args = AutomationArguments.Parse(text);

        Assert.Empty(args.HostProfiles);
        Assert.Null(args.BuildProfile);
        Assert.False(args.Force);
        Assert.Null(args.RequestId);
        Assert.Empty(args.Errors);
    }

    [Theory]
    [InlineData("/verbose")]
    [InlineData("/id:")]
    [InlineData("/build")]
    [InlineData("/force:yes")]
    public void Parse_ReportsBadSwitches(string text)
    {
        var error = Assert.Single(AutomationArguments.Parse(text).Errors);
        Assert.Contains(text, error);
    }
}

public class ProfileChoiceTests
{
    private static readonly string[] Available = { "default", "debug", "gcc-13" };

    [Fact]
    public void Validate_AcceptsKnownProfilesAndRemovesDuplicates()
    {
        Assert.Null(ProfileChoice.Validate(Available, new[] { "debug", " debug ", "default" }, "default", out var hosts));
        Assert.Equal(new[] { "debug", "default" }, hosts);
    }

    [Fact]
    public void Validate_RejectsEmptyHosts()
    {
        Assert.Equal("Choose at least one host profile.", ProfileChoice.Validate(Available, new[] { " " }, "default", out _));
    }

    [Fact]
    public void Validate_NamesUnknownProfilesAndListsAvailable()
    {
        var error = ProfileChoice.Validate(Available, new[] { "debug", "clang" }, "msvc", out _);

        Assert.Equal("Unknown Conan profile(s): clang, msvc. Available profiles: default, debug, gcc-13.", error);
    }
}

public class StatusAndResultTests
{
    [Fact]
    public void ReadConfigurePresetNames_ReadsNamesAndToleratesBadFiles()
    {
        using var dir = new TempDirectory();
        var presets = dir.Write("CMakePresets.json", "{ \"configurePresets\": [ { \"name\": \"conan-default-release\" } ] }");

        Assert.Equal(new[] { "conan-default-release" }, UserPresetsFile.ReadConfigurePresetNames(presets));
        Assert.Empty(UserPresetsFile.ReadConfigurePresetNames(dir.Write("broken.json", "{")));
        Assert.Empty(UserPresetsFile.ReadConfigurePresetNames(Path.Combine(dir.Path, "missing.json")));
    }

    [Fact]
    public async System.Threading.Tasks.Task ProjectStatus_ReflectsSettingsPresetsAndPause()
    {
        using var dir = new TempDirectory();
        dir.Write("CMakeLists.txt");
        dir.Write("conanfile.txt", "[layout]\ncmake_layout\n");
        var presets = dir.Write(Path.Combine("build", "default", "Release", "generators", "CMakePresets.json"),
            "{ \"configurePresets\": [ { \"name\": \"conan-default-release\" } ] }");
        UserPresetsFile.Write(dir.Path, new[] { presets });
        new VsConanSettings { HostProfiles = { "default" } }.Save(dir.Path);
        new CMakeGate(dir.Path).Close();

        // No install has been recorded, so the status is "not up to date" without running Conan.
        var status = await ProjectStatus.CreateAsync(
            ConanProject.Find(dir.Path)!, new ConanCli("conan", dir.Path), new[] { "default", "debug" }, System.Threading.CancellationToken.None);

        Assert.Equal("conanfile.txt", status.Conanfile);
        Assert.True(status.UsesCmakeLayout);
        Assert.Equal(new[] { "default" }, status.HostProfiles);
        Assert.False(status.UpToDate); // no install recorded yet
        Assert.True(status.CMakePaused);
        Assert.Equal(new[] { "conan-default-release" }, status.ConfigurePresets);
        Assert.Equal(new[] { "default", "debug" }, status.AvailableProfiles);
    }

    [Fact]
    public void CommandResult_WritesTheDocumentedJson()
    {
        using var dir = new TempDirectory();
        new CommandResult
        {
            Id = "42",
            Command = "Conan.SelectProfiles",
            State = CommandResult.Failed,
            StartedAt = DateTimeOffset.Now,
            CompletedAt = DateTimeOffset.Now,
            Error = "Choose at least one host profile.",
        }.Save(dir.Path);

        var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(CommandResult.GetPath(dir.Path)));

        Assert.Equal("42", (string?)json["id"]);
        Assert.Equal("Conan.SelectProfiles", (string?)json["command"]);
        Assert.Equal("failed", (string?)json["state"]);
        Assert.Equal("Choose at least one host profile.", (string?)json["error"]);
        Assert.NotNull(json["startedAt"]);
        Assert.Null(json["result"]);
    }

    [Fact]
    public void CommandResult_KeepsEachRequestsResultInItsOwnFile()
    {
        using var dir = new TempDirectory();
        new CommandResult { Id = "first", Command = "Conan.InstallDependencies", StartedAt = DateTimeOffset.Now }.Save(dir.Path);
        new CommandResult { Id = "second", Command = "Conan.ShowStatus", State = CommandResult.Succeeded, StartedAt = DateTimeOffset.Now }.Save(dir.Path);

        var first = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(CommandResult.GetPath(dir.Path, "first")!));
        var latest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(CommandResult.GetPath(dir.Path)));

        Assert.Equal("running", (string?)first["state"]);
        Assert.Equal("Conan.InstallDependencies", (string?)first["command"]);
        Assert.Equal("second", (string?)latest["id"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("a b")]
    public void CommandResult_HasNoOwnFileForUnsafeIds(string? id)
    {
        Assert.Null(CommandResult.GetPath(@"C:\project", id));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Unattended_ReadsTheEnvironmentVariable(string? value, bool expected)
    {
        Assert.Equal(expected, Unattended.IsSet(name => name == Unattended.VariableName ? value : null));
    }
}
