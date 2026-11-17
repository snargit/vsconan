using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VsConan.Core;

namespace VsConan.Core.Tests;

/// <summary>Requires Conan 2 on PATH with a 'default' profile. Uses no remote packages.</summary>
[Trait("Category", "Integration")]
public class DependencySyncIntegrationTests
{
    private static ConanProject CreateProject(TempDirectory dir)
    {
        File.WriteAllText(Path.Combine(dir.Path, "conanfile.txt"), "[generators]\nCMakeToolchain\nCMakeDeps\n[layout]\ncmake_layout\n");
        File.WriteAllText(Path.Combine(dir.Path, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.23)\nproject(app CXX)\n");
        return ConanProject.Find(dir.Path)!;
    }

    [Fact]
    public async Task RunAsync_PublishesPresetsOnceAndResumesCMake()
    {
        using var dir = new TempDirectory();
        var project = CreateProject(dir);
        var settings = new VsConanSettings { HostProfiles = { "default" } };
        var output = new List<string>();
        var gate = new CMakeGate(dir.Path);
        var gateStates = new List<bool>();

        var result = await new DependencySync(new ConanCli("conan", dir.Path)).RunAsync(
            project, settings, force: false,
            line =>
            {
                lock (output)
                {
                    output.Add(line);
                    // Conan is running: CMake must be paused and the user presets not yet written.
                    if (line.StartsWith("=========="))
                    {
                        gateStates.Add(gate.IsCMakeEnabled);
                        Assert.False(File.Exists(UserPresetsFile.GetPath(dir.Path)));
                    }
                }
            },
            CancellationToken.None);

        Assert.Equal(SyncOutcome.Installed, result.Outcome);
        Assert.Equal(new[] { false }, gateStates);
        Assert.True(gate.IsCMakeEnabled);

        var includes = UserPresetsFile.ReadIncludes(dir.Path);
        Assert.Single(includes);
        Assert.Contains(Path.Combine("build", "default"), includes[0]);
        Assert.True(File.Exists(includes[0]));
        Assert.True(await DependencySync.IsUpToDateAsync(project, settings, new ConanCli("conan", dir.Path), CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_SkipsInstallWhenUpToDate()
    {
        using var dir = new TempDirectory();
        var project = CreateProject(dir);
        var settings = new VsConanSettings { HostProfiles = { "default" } };
        var sync = new DependencySync(new ConanCli("conan", dir.Path));
        await sync.RunAsync(project, settings, force: false, _ => { }, CancellationToken.None);

        // Simulate the folder closing, then reopening with nothing changed.
        var gate = new CMakeGate(dir.Path);
        gate.Close();
        var result = await sync.RunAsync(project, settings, force: false, _ => { }, CancellationToken.None);

        Assert.Equal(SyncOutcome.UpToDate, result.Outcome);
        Assert.True(gate.IsCMakeEnabled);
    }

    [Fact]
    public async Task RunAsync_ReportsConansErrorForAFailedProfile()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "conanfile.txt"), "[layout]\ncmake_layout\n[nosuchsection]\n");
        File.WriteAllText(Path.Combine(dir.Path, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.23)\nproject(app CXX)\n");
        var project = ConanProject.Find(dir.Path)!;

        var result = await new DependencySync(new ConanCli("conan", dir.Path)).RunAsync(
            project, new VsConanSettings { HostProfiles = { "default" } }, force: false, _ => { }, CancellationToken.None);

        Assert.Equal(SyncOutcome.PartiallyFailed, result.Outcome);
        var profile = Assert.Single(result.Profiles);
        Assert.Contains("ERROR", profile.ErrorOutput);
        Assert.Contains("nosuchsection", profile.ErrorOutput);
    }

    [Fact]
    public async Task RunAsync_ReportsTheFindPackageNamesAndTargetsOfDirectDependencies()
    {
        // A header-only package (nothing to compile), whose CMake names differ from its package name.
        using var dir = new TempDirectory();
        dir.Write(Path.Combine("dep", "conanfile.py"), """
            from conan import ConanFile

            class Dep(ConanFile):
                name = "vsconan-testdep"
                version = "0.1"
                package_type = "header-library"

                def package_info(self):
                    self.cpp_info.bindirs = []
                    self.cpp_info.libdirs = []
                    self.cpp_info.set_property("cmake_file_name", "VsConanTestDep")
                    self.cpp_info.set_property("cmake_target_name", "VsConanTestDep::headers")
            """);
        var app = Path.Combine(dir.Path, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "conanfile.txt"),
            "[requires]\nvsconan-testdep/0.1\n[generators]\nCMakeToolchain\nCMakeDeps\n[layout]\ncmake_layout\n");
        File.WriteAllText(Path.Combine(app, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.23)\nproject(app CXX)\n");

        var export = await ProcessRunner.RunAsync("conan", new[] { "export", Path.Combine(dir.Path, "dep") }, dir.Path, null, CancellationToken.None);
        Assert.Equal(0, export.ExitCode);
        try
        {
            var project = ConanProject.Find(app)!;
            var settings = new VsConanSettings { HostProfiles = { "default" } };
            settings.Save(app);
            var cli = new ConanCli("conan", app);
            var sync = new DependencySync(cli);
            var result = await sync.RunAsync(project, settings, force: false, _ => { }, CancellationToken.None);

            Assert.Equal(SyncOutcome.Installed, result.Outcome);
            AssertUsage(InstallReport.Create(project, result).CMakeUsage);

            // Remembered: an up-to-date install, and the status, still report the names.
            var again = await sync.RunAsync(project, settings, force: false, _ => { }, CancellationToken.None);
            Assert.Equal(SyncOutcome.UpToDate, again.Outcome);
            AssertUsage(InstallReport.Create(project, again).CMakeUsage);
            AssertUsage((await ProjectStatus.CreateAsync(project, cli, null, CancellationToken.None)).CMakeUsage);

            static void AssertUsage(Dictionary<string, CMakeUsage>? byProfile)
            {
                var usage = Assert.Single(byProfile!);
                Assert.Equal("default", usage.Key);
                Assert.Equal(new[] { "VsConanTestDep" }, usage.Value.FindPackages);
                Assert.Equal(new[] { "VsConanTestDep::headers" }, usage.Value.Targets);
            }
        }
        finally
        {
            await ProcessRunner.RunAsync("conan", new[] { "remove", "vsconan-testdep/*", "-c" }, dir.Path, null, CancellationToken.None);
        }
    }

    [Fact]
    public async Task RunAsync_RefusesToOverwriteHandWrittenUserPresets()
    {
        using var dir = new TempDirectory();
        var project = CreateProject(dir);
        const string handWritten = "{ \"version\": 4, \"configurePresets\": [] }";
        File.WriteAllText(UserPresetsFile.GetPath(dir.Path), handWritten);

        var result = await new DependencySync(new ConanCli("conan", dir.Path)).RunAsync(
            project, new VsConanSettings { HostProfiles = { "default" } }, force: true, _ => { }, CancellationToken.None);

        Assert.Equal(SyncOutcome.UserPresetsNotOwned, result.Outcome);
        Assert.Equal(handWritten, File.ReadAllText(UserPresetsFile.GetPath(dir.Path)));
    }
}
