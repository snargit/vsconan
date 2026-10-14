using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VsConan.Core;

namespace VsConan.Core.Tests;

/// <summary>Requires Conan 2 on PATH. Uses a Conan home of the test's own, via .conanrc.</summary>
[Trait("Category", "Integration")]
public class ChangeDetectionIntegrationTests
{
    [Fact]
    public async Task IsUpToDate_UsesTheProjectsConanrcAndNoticesProfileEdits()
    {
        using var dir = new TempDirectory();
        dir.Write("conanfile.txt", "[generators]\nCMakeToolchain\nCMakeDeps\n[layout]\ncmake_layout\n");
        dir.Write("CMakeLists.txt", "cmake_minimum_required(VERSION 3.23)\nproject(app CXX)\n");
        // A Conan home of the project's own, so the user's real profiles are never touched.
        dir.Write(".conanrc", "conan_home=./conan-home\n");
        var cli = new ConanCli("conan", dir.Path);

        Assert.EndsWith("conan-home", await cli.ConfigHomeAsync(CancellationToken.None));
        Assert.Equal(0, Run("conan", "profile detect", dir.Path));
        var profile = Path.Combine(dir.Path, "conan-home", "profiles", "default");
        Assert.True(File.Exists(profile));

        var project = ConanProject.Find(dir.Path)!;
        var settings = new VsConanSettings { HostProfiles = { "default" } };
        var result = await new DependencySync(cli).RunAsync(project, settings, force: false, _ => { }, CancellationToken.None);
        Assert.Equal(SyncOutcome.Installed, result.Outcome);
        Assert.True(await DependencySync.IsUpToDateAsync(project, settings, cli, CancellationToken.None));

        // Saving the profile unchanged isn't a change; editing what it resolves to is.
        File.WriteAllBytes(profile, File.ReadAllBytes(profile));
        Assert.True(await DependencySync.IsUpToDateAsync(project, settings, cli, CancellationToken.None));
        File.AppendAllText(profile, "\n[conf]\ntools.build:jobs=3\n");
        Assert.False(await DependencySync.IsUpToDateAsync(project, settings, cli, CancellationToken.None));
    }

    private static int Run(string fileName, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}

public class ConanInputWatcherTests
{
    [Fact]
    public async Task ReportsABurstOfChangesOnce_AndIgnoresOtherFiles()
    {
        using var dir = new TempDirectory();
        var home = Path.Combine(dir.Path, "home");
        Directory.CreateDirectory(Path.Combine(home, "profiles"));
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(project);

        var reports = new List<IReadOnlyList<string>>();
        var reported = new SemaphoreSlim(0);
        using var watcher = new ConanInputWatcher(project, _ => Task.FromResult<string?>(home), reasons =>
        {
            lock (reports) reports.Add(reasons);
            reported.Release();
        })
        {
            DebounceInterval = TimeSpan.FromMilliseconds(300),
        };
        await watcher.StartAsync();
        Assert.Equal(Path.Combine(home, "profiles"), watcher.ProfilesFolder);

        File.WriteAllText(Path.Combine(project, "main.cpp"), "int main() {}");          // not a Conan input
        File.WriteAllText(Path.Combine(project, "conanfile.txt"), "[requires]\n");
        File.AppendAllText(Path.Combine(project, "conanfile.txt"), "zlib/1.3\n");
        File.WriteAllText(Path.Combine(home, "profiles", "debug"), "[settings]\n");

        Assert.True(await reported.WaitAsync(TimeSpan.FromSeconds(10)), "no change reported");
        await Task.Delay(800);
        lock (reports)
        {
            var report = Assert.Single(reports);
            Assert.Contains("conanfile.txt changed", report);
            Assert.Contains("Conan profile 'debug' changed", report);
            Assert.DoesNotContain(report, reason => reason.Contains("main.cpp"));
        }
    }
}
