using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VsConan.Core;
using Task = System.Threading.Tasks.Task;

namespace VsConan;

/// <summary>
/// Keeps an opened folder's Conan dependencies installed ahead of VS's CMake integration, and
/// implements the Conan menu commands. VS does the configure and build from the
/// CMakeUserPresets.json that <see cref="DependencySync"/> writes.
/// </summary>
internal sealed class ConanController
{
    private readonly AsyncPackage _package;
    private readonly ConanOutputPane _output;
    private readonly ConanCli _cli = new();
    private CancellationTokenSource _folderSession = new();
    private string? _openFolder;
    private int _busy;

    public ConanController(AsyncPackage package, ConanOutputPane output)
    {
        _package = package;
        _output = output;
    }

    /// <summary>
    /// True when the open folder is one VsConan manages (conanfile + CMakeLists.txt). Checked live,
    /// so the commands appear as soon as the files are added.
    /// </summary>
    public bool IsActive => ConanProject.IsConanCMakeProject(_openFolder);

    /// <summary>
    /// Folders VsConan manages are left with CMake paused when they close (see
    /// <see cref="OnBeforeFolderClosed"/>), so this runs before VS configures anything.
    /// Any other folder is left alone.
    /// </summary>
    public async Task OnFolderOpenedAsync(string folder, CancellationToken cancellationToken)
    {
        _folderSession = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _openFolder = folder;

        try
        {
            var project = ConanProject.Find(folder);
            if (project is null)
            {
                // Not ours. The only thing to do is undo our own pause, should the folder have
                // stopped being a Conan project (e.g. the conanfile was deleted) since we closed it.
                // Open() writes nothing unless VsConan paused this folder.
                new CMakeGate(folder).Open();
                return;
            }

            var settings = VsConanSettings.Load(project.RootDirectory);
            if (settings.HostProfiles.Count == 0)
            {
                await RunFirstTimeSetupAsync(project, settings, cancellationToken);
                return;
            }

            await SyncAsync(project, settings, force: false);
        }
        catch (Exception ex)
        {
            ReportException(ex);
        }
    }

    /// <summary>
    /// The first time a Conan folder opens, asks for profiles in a window rather than pointing at the
    /// menu. Shown once per folder: after "Not now", the folder opens normally and the menu is the way in.
    /// </summary>
    private async Task RunFirstTimeSetupAsync(ConanProject project, VsConanSettings settings, CancellationToken cancellationToken)
    {
        var directory = project.RootDirectory;
        var gate = new CMakeGate(directory);
        if (VsConanState.Load(directory).FirstRunPromptShown)
        {
            gate.Open();
            return;
        }

        // Configuring now would run without the Conan toolchain, so pause while the user decides.
        gate.Close();

        VsConanSettings? chosen = null;
        try
        {
            chosen = await PromptForSettingsAsync(project, settings, cancellationToken, firstRun: true);

            // Recorded only once the prompt has run, so a failure to list profiles (e.g. Conan not on
            // PATH) means the window is offered again next time.
            var state = VsConanState.Load(directory);
            state.FirstRunPromptShown = true;
            state.Save(directory);
        }
        finally
        {
            if (chosen is null)
            {
                gate.Open();
            }
        }

        if (chosen is null)
        {
            _output.WriteLine("Conan dependencies weren't installed. To set them up later, use Tools > Conan > Select Profiles...");
            return;
        }

        await SyncAsync(project, chosen, force: false);
    }

    /// <summary>
    /// Pauses CMake so that the next time this folder opens, Conan runs before VS configures.
    /// There is no "before folder open" event, so this is the only point we're guaranteed to run first.
    /// </summary>
    public void OnBeforeFolderClosed(string folder)
    {
        _folderSession.Cancel();
        _openFolder = null;

        try
        {
            var project = ConanProject.Find(folder);
            if (project is not null && VsConanSettings.Load(project.RootDirectory).HostProfiles.Count > 0)
            {
                new CMakeGate(project.RootDirectory).Close();
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"VsConan: couldn't pause CMake integration for '{folder}': {ex.Message}");
        }
    }

    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            var project = await FindOpenProjectAsync();
            if (project is null)
            {
                return;
            }

            var settings = VsConanSettings.Load(project.RootDirectory);
            if (settings.HostProfiles.Count == 0)
            {
                settings = await PromptForSettingsAsync(project, settings, cancellationToken);
                if (settings is null)
                {
                    return;
                }
            }

            await SyncAsync(project, settings, force: true);
        }
        catch (Exception ex)
        {
            ReportException(ex);
        }
    }

    public async Task SelectProfilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var project = await FindOpenProjectAsync();
            if (project is null)
            {
                return;
            }

            var settings = await PromptForSettingsAsync(project, VsConanSettings.Load(project.RootDirectory), cancellationToken);
            if (settings is not null)
            {
                // The fingerprint includes the profiles, so this installs only if the selection changed.
                await SyncAsync(project, settings, force: false);
            }
        }
        catch (Exception ex)
        {
            ReportException(ex);
        }
    }

    private async Task SyncAsync(ConanProject project, VsConanSettings settings, bool force)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            _output.WriteLine("A Conan install is already running.");
            return;
        }

        var cancellationToken = _folderSession.Token;
        try
        {
            var willInstall = force || !DependencySync.IsUpToDate(project, settings);
            if (willInstall)
            {
                if (!project.UsesCmakeLayout)
                {
                    _output.WriteLine($"Warning: {project.ConanfilePath} doesn't appear to use cmake_layout. "
                        + "Without it, CMake presets for different profiles will have clashing names.");
                }

                await _output.ActivateAsync();
                await SetStatusTextAsync("Conan: installing dependencies (CMake paused)...");
            }

            await TaskScheduler.Default;
            var result = await new DependencySync(_cli).RunAsync(project, settings, force, _output.WriteLine, cancellationToken);
            ReportResult(result);
            await OfferTargetsViewOnceAsync(project, result);

            if (willInstall)
            {
                var failed = result.Profiles.Count(r => !r.Succeeded);
                await SetStatusTextAsync(result.Outcome switch
                {
                    SyncOutcome.Installed => "Conan: dependencies installed",
                    SyncOutcome.PartiallyFailed => $"Conan: {failed} of {result.Profiles.Count} profile(s) failed",
                    _ => "Conan: install skipped",
                });
            }
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine("Conan install cancelled; it will run again the next time this folder opens.");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// After a folder's first successful install, offers to switch Solution Explorer to the CMake
    /// Targets View (the Folder View is the default). Offered once per folder.
    /// </summary>
    private async Task OfferTargetsViewOnceAsync(ConanProject project, SyncResult result)
    {
        if (!result.Profiles.Any(r => r.Succeeded))
        {
            return;
        }

        var state = VsConanState.Load(project.RootDirectory);
        if (state.TargetsViewHintShown)
        {
            return;
        }

        state.TargetsViewHintShown = true;
        state.Save(project.RootDirectory);
        await TargetsViewInfoBar.ShowAsync(_package, _output);
    }

    /// <summary>Shows the profile picker; saves and returns the new settings, or null if cancelled.</summary>
    private async Task<VsConanSettings?> PromptForSettingsAsync(
        ConanProject project, VsConanSettings current, CancellationToken cancellationToken, bool firstRun = false)
    {
        await TaskScheduler.Default;
        var profiles = await _cli.ListProfilesAsync(cancellationToken);

        await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (profiles.Count == 0)
        {
            _output.WriteLine("No Conan profiles found. Create one with 'conan profile detect'.");
            await _output.ActivateAsync();
            return null;
        }

        var dialog = new ProfileSelectionDialog(profiles, current, firstRun ? project : null);
        if (dialog.ShowModal() != true)
        {
            return null;
        }

        var settings = dialog.GetSettings();
        settings.Save(project.RootDirectory);
        return settings;
    }

    private async Task<ConanProject?> FindOpenProjectAsync()
    {
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

        // In Open Folder mode the "solution directory" is the opened folder.
        string? directory = null;
        if (await _package.GetServiceAsync(typeof(SVsSolution)) is IVsSolution solution
            && ErrorHandler.Succeeded(solution.GetSolutionInfo(out var solutionDirectory, out _, out _)))
        {
            directory = solutionDirectory;
        }

        var project = ConanProject.Find(directory);
        if (project is null)
        {
            _output.WriteLine(string.IsNullOrEmpty(directory)
                ? "No folder is open. Open the folder containing your conanfile (File > Open > Folder)."
                : $"'{directory}' isn't a Conan CMake project: it needs a conanfile.py or conanfile.txt "
                    + "and a CMakeLists.txt at its root.");
            await _output.ActivateAsync();
        }

        return project;
    }

    private void ReportResult(SyncResult result)
    {
        if (result.Outcome is SyncOutcome.UpToDate or SyncOutcome.UserPresetsNotOwned)
        {
            return;
        }

        _output.WriteLine("========== Conan install summary ==========");
        foreach (var profile in result.Profiles)
        {
            _output.WriteLine(profile.Succeeded
                ? $"  {profile.Profile}: succeeded"
                : $"  {profile.Profile}: FAILED (exit code {profile.ExitCode})");
        }

        if (result.Profiles.Any(r => r.Succeeded))
        {
            _output.WriteLine("CMake integration resumed. Choose a 'conan-<profile>-<build type>' configuration "
                + "from the toolbar to configure and build.");
        }
    }

    private void ReportException(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return;
        }

        if (ex is Win32Exception)
        {
            _output.WriteLine($"Could not run '{_cli.Executable}': {ex.Message}. Make sure Conan 2 is installed and on PATH.");
        }
        else
        {
            _output.WriteLine($"VsConan error: {ex}");
        }

        _package.JoinableTaskFactory.RunAsync(_output.ActivateAsync).FileAndForget("VsConan/ActivateOutput");
    }

    private async Task SetStatusTextAsync(string text)
    {
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await _package.GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar statusBar)
        {
            statusBar.SetText(text);
        }
    }
}
