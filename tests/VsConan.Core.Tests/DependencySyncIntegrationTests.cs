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

        var result = await new DependencySync(new ConanCli()).RunAsync(
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
        Assert.True(DependencySync.IsUpToDate(project, settings));
    }

    [Fact]
    public async Task RunAsync_SkipsInstallWhenUpToDate()
    {
        using var dir = new TempDirectory();
        var project = CreateProject(dir);
        var settings = new VsConanSettings { HostProfiles = { "default" } };
        var sync = new DependencySync(new ConanCli());
        await sync.RunAsync(project, settings, force: false, _ => { }, CancellationToken.None);

        // Simulate the folder closing, then reopening with nothing changed.
        var gate = new CMakeGate(dir.Path);
        gate.Close();
        var result = await sync.RunAsync(project, settings, force: false, _ => { }, CancellationToken.None);

        Assert.Equal(SyncOutcome.UpToDate, result.Outcome);
        Assert.True(gate.IsCMakeEnabled);
    }

    [Fact]
    public async Task RunAsync_RefusesToOverwriteHandWrittenUserPresets()
    {
        using var dir = new TempDirectory();
        var project = CreateProject(dir);
        const string handWritten = "{ \"version\": 4, \"configurePresets\": [] }";
        File.WriteAllText(UserPresetsFile.GetPath(dir.Path), handWritten);

        var result = await new DependencySync(new ConanCli()).RunAsync(
            project, new VsConanSettings { HostProfiles = { "default" } }, force: true, _ => { }, CancellationToken.None);

        Assert.Equal(SyncOutcome.UserPresetsNotOwned, result.Outcome);
        Assert.Equal(handWritten, File.ReadAllText(UserPresetsFile.GetPath(dir.Path)));
    }
}
