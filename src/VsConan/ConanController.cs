using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
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

    /// <summary>
    /// Conan.InstallDependencies. With arguments (automation) it runs without UI and reports to
    /// <see cref="CommandResult"/>; without, it's the menu command.
    /// </summary>
    public Task InstallAsync(string? arguments, CancellationToken cancellationToken) =>
        IsAutomation(arguments)
            ? RunAutomationAsync("Conan.InstallDependencies", arguments!, AutomateInstallAsync)
            : InstallInteractiveAsync(cancellationToken);

    /// <summary>Conan.SelectProfiles. With arguments it saves the given profiles without UI (and doesn't install).</summary>
    public Task SelectProfilesAsync(string? arguments, CancellationToken cancellationToken) =>
        IsAutomation(arguments)
            ? RunAutomationAsync("Conan.SelectProfiles", arguments!, AutomateSelectProfilesAsync)
            : SelectProfilesInteractiveAsync(cancellationToken);

    /// <summary>Conan.ShowStatus: writes the status to the Conan output pane, and to <see cref="CommandResult"/>.</summary>
    public Task ShowStatusAsync(string? arguments) =>
        RunAutomationAsync("Conan.ShowStatus", arguments ?? string.Empty, AutomateShowStatusAsync, showOutput: true);

    private static bool IsAutomation(string? arguments) => !string.IsNullOrWhiteSpace(arguments);

    private async Task InstallInteractiveAsync(CancellationToken cancellationToken)
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

    private async Task SelectProfilesInteractiveAsync(CancellationToken cancellationToken)
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

    /// <summary>
    /// Runs a command for automation: no UI, and the outcome is written to .vs/VsConan/command-result.json
    /// ("running", then "succeeded" or "failed") because VS commands can't return values.
    /// </summary>
    private async Task RunAutomationAsync(
        string command,
        string arguments,
        Func<ConanProject, AutomationArguments, Task<object>> body,
        bool showOutput = false)
    {
        var parsed = AutomationArguments.Parse(arguments);
        var project = ConanProject.Find(_openFolder);
        if (project is null)
        {
            // Not reachable through VS (the commands are disabled then), but keep a trace if it is.
            _output.WriteLine($"{command}: no Conan CMake project is open.");
            return;
        }

        var directory = project.RootDirectory;
        var result = new CommandResult { Id = parsed.RequestId, Command = command, StartedAt = DateTimeOffset.Now };
        try
        {
            result.Save(directory);
            if (parsed.Errors.Count > 0)
            {
                throw new AutomationException(string.Join(" ", parsed.Errors));
            }

            result.Result = await body(project, parsed);
            result.State = CommandResult.Succeeded;
        }
        catch (Exception ex)
        {
            result.State = CommandResult.Failed;
            result.Error = DescribeForCaller(ex);
        }

        result.CompletedAt = DateTimeOffset.Now;
        result.Save(directory);

        var id = parsed.RequestId is null ? string.Empty : $" [{parsed.RequestId}]";
        _output.WriteLine($"{command}{id}: {result.State}{(result.Error is null ? string.Empty : $": {result.Error}")}");
        if (showOutput && result.Result is not null)
        {
            _output.WriteLine(JsonConvert.SerializeObject(result.Result, Formatting.Indented));
            await _output.ActivateAsync();
        }
    }

    private async Task<object> AutomateSelectProfilesAsync(ConanProject project, AutomationArguments arguments)
    {
        if (arguments.HostProfiles.Count == 0)
        {
            throw new AutomationException(
                "Pass the host profiles to install, e.g. Conan.SelectProfiles default debug /build:default.");
        }

        await TaskScheduler.Default;
        var available = await _cli.ListProfilesAsync(_folderSession.Token);
        var current = VsConanSettings.Load(project.RootDirectory);
        var buildProfile = arguments.BuildProfile ?? current.BuildProfile;
        var error = ProfileChoice.Validate(available, arguments.HostProfiles, buildProfile, out var hosts);
        if (error is not null)
        {
            throw new AutomationException(error);
        }

        new VsConanSettings { HostProfiles = hosts, BuildProfile = buildProfile }.Save(project.RootDirectory);

        // Profiles are chosen now, so the first-run window has nothing left to offer.
        var state = VsConanState.Load(project.RootDirectory);
        state.FirstRunPromptShown = true;
        state.Save(project.RootDirectory);

        return ProjectStatus.Create(project, available);
    }

    private async Task<object> AutomateInstallAsync(ConanProject project, AutomationArguments arguments)
    {
        var settings = VsConanSettings.Load(project.RootDirectory);
        if (settings.HostProfiles.Count == 0)
        {
            throw new AutomationException(
                "No Conan profiles are selected for this folder. Run Conan.SelectProfiles <profiles> first.");
        }

        var result = await SyncCoreAsync(project, settings, arguments.Force);
        return InstallReport.Create(project, result);
    }

    private async Task<object> AutomateShowStatusAsync(ConanProject project, AutomationArguments arguments)
    {
        await TaskScheduler.Default;
        var available = await _cli.ListProfilesAsync(_folderSession.Token);
        return ProjectStatus.Create(project, available);
    }

    private string DescribeForCaller(Exception ex) => ex switch
    {
        AutomationException or InvalidOperationException => ex.Message,
        OperationCanceledException => "Cancelled: the folder is closing or Visual Studio is exiting.",
        Win32Exception => $"Could not run '{_cli.Executable}': {ex.Message}. Make sure Conan 2 is installed and on PATH.",
        _ => ex.Message,
    };

    private const string AlreadyRunning = "A Conan install is already running.";

    /// <summary>The menu commands' install: reports to the Conan output pane rather than throwing.</summary>
    private async Task SyncAsync(ConanProject project, VsConanSettings settings, bool force)
    {
        try
        {
            await SyncCoreAsync(project, settings, force);
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine("Conan install cancelled; it will run again the next time this folder opens.");
        }
        catch (InvalidOperationException ex) when (ex.Message == AlreadyRunning)
        {
            _output.WriteLine(ex.Message);
        }
    }

    private async Task<SyncResult> SyncCoreAsync(ConanProject project, VsConanSettings settings, bool force)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            throw new InvalidOperationException(AlreadyRunning);
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

            return result;
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

    /// <summary>A problem with an automation request; the message is for the caller.</summary>
    private sealed class AutomationException : Exception
    {
        public AutomationException(string message)
            : base(message)
        {
        }
    }
}
