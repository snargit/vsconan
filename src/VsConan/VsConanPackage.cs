using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VsConan;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuids.PackageString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class VsConanPackage : AsyncPackage
{
    private ConanController? _controller;
    private IVsSolution? _solution;
    private uint _solutionEventsCookie;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var output = await ConanOutputPane.CreateAsync(this);
        var controller = _controller = new ConanController(this, output);

        var commandService = (OleMenuCommandService?)await GetServiceAsync(typeof(IMenuCommandService))
            ?? throw new InvalidOperationException("IMenuCommandService is unavailable.");

        AddCommand(commandService, PackageIds.InstallDependenciesCommand, args => controller.InstallAsync(args, DisposalToken));
        AddCommand(commandService, PackageIds.SelectProfilesCommand, args => controller.SelectProfilesAsync(args, DisposalToken));
        AddCommand(commandService, PackageIds.ShowStatusCommand, controller.ShowStatusAsync);

        _solution = (IVsSolution?)await GetServiceAsync(typeof(SVsSolution))
            ?? throw new InvalidOperationException("SVsSolution is unavailable.");

        var events = new FolderEvents(
            folder => _ = JoinableTaskFactory.RunAsync(() => controller.OnFolderOpenedAsync(folder, DisposalToken)),
            controller.OnBeforeFolderClosed);
        _solution.AdviseSolutionEvents(events, out _solutionEventsCookie);

        // We auto-load on FolderOpened, so the folder that caused the load opened before we subscribed.
        if (ErrorHandler.Succeeded(_solution.GetProperty((int)__VSPROPID7.VSPROPID_IsInOpenFolderMode, out var inOpenFolder))
            && inOpenFolder is true
            && ErrorHandler.Succeeded(_solution.GetSolutionInfo(out var folder, out _, out _)))
        {
            _ = JoinableTaskFactory.RunAsync(() => controller.OnFolderOpenedAsync(folder, DisposalToken));
        }
    }

    /// <summary>
    /// Adds a command that is only visible (and enabled) when the open folder is a Conan CMake project.
    /// <paramref name="execute"/> gets the command's arguments: null from the menu, the argument string
    /// when automation runs it by name (e.g. DTE.ExecuteCommand("Conan.InstallDependencies", "/id:42")).
    /// </summary>
    private void AddCommand(OleMenuCommandService commandService, int id, Func<string?, Task> execute)
    {
        var command = new OleMenuCommand(
            (_, e) =>
            {
                var arguments = (e as OleMenuCmdEventArgs)?.InValue as string;
                _ = JoinableTaskFactory.RunAsync(() => execute(arguments));
            },
            new CommandID(PackageGuids.CommandSet, id))
        {
            // With AllowParams in the .vsct, this is what makes VS pass arguments through: VS asks the
            // command for its parameter list and, without one, rejects arguments ("does not accept
            // arguments or switches"). "$" means any argument string.
            ParametersDescription = "$",
        };
        command.BeforeQueryStatus += (_, _) => command.Visible = command.Enabled = _controller?.IsActive == true;
        commandService.AddCommand(command);
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposing && _solution is not null && _solutionEventsCookie != 0)
        {
            _solution.UnadviseSolutionEvents(_solutionEventsCookie);
            _solutionEventsCookie = 0;
        }

        base.Dispose(disposing);
    }
}
