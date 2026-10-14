using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VsConan;

/// <summary>
/// Info bar offering to switch Solution Explorer to the CMake Targets View, which is a better fit than
/// the Folder View for a CMake project. Shown once per folder, after its first successful install.
/// </summary>
internal sealed class TargetsViewInfoBar : IVsInfoBarUIEvents
{
    /// <summary>Where to find the view by hand, for when switching automatically isn't possible.</summary>
    public const string ManualHint =
        "Tip: to see this project's CMake targets, click Solution Explorer's 'Switch Views' button and choose 'CMake Targets View'.";

    // The CMake package's own "switch to CMake Targets View" command (guidCMakeMainMenuCmdSet, id 34),
    // the same in VS 2022 and VS 2026. It isn't a documented API, hence the fallback to ManualHint.
    private static readonly Guid CMakeMainMenuCmdSet = new("3FDA7568-7098-4374-A30C-D83732080442");
    private const uint SwitchToTargetsViewCommandId = 34;

    private readonly ConanOutputPane _output;
    private uint _cookie;

    private TargetsViewInfoBar(ConanOutputPane output)
    {
        _output = output;
    }

    public static async Task ShowAsync(AsyncPackage package, ConanOutputPane output)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (await package.GetServiceAsync(typeof(SVsShell)) is not IVsShell shell
            || await package.GetServiceAsync(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory
            || ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var hostObject))
            || hostObject is not IVsInfoBarHost host)
        {
            output.WriteLine(ManualHint);
            return;
        }

        var model = new InfoBarModel(
            new[] { new InfoBarTextSpan("Conan dependencies are installed. The CMake Targets View in Solution Explorer lists this project's targets. ") },
            new[] { new InfoBarHyperlink("Switch to CMake Targets View") },
            KnownMonikers.StatusInformation,
            isCloseButtonVisible: true);

        var element = factory.CreateInfoBar(model);
        var events = new TargetsViewInfoBar(output);
        element.Advise(events, out events._cookie);
        host.AddInfoBar(element);
    }

    public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        infoBarUIElement.Close();

        if (!TrySwitchToTargetsView())
        {
            _output.WriteLine(ManualHint);
        }
    }

    public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        infoBarUIElement.Unadvise(_cookie);
    }

    private static bool TrySwitchToTargetsView()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell uiShell)
        {
            var solutionExplorer = new Guid(ToolWindowGuids80.SolutionExplorer);
            if (ErrorHandler.Succeeded(uiShell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref solutionExplorer, out var frame)))
            {
                frame?.Show();
            }
        }

        if (Package.GetGlobalService(typeof(SUIHostCommandDispatcher)) is not IOleCommandTarget dispatcher)
        {
            return false;
        }

        var group = CMakeMainMenuCmdSet;
        return dispatcher.Exec(ref group, SwitchToTargetsViewCommandId, (uint)OLECMDEXECOPT.OLECMDEXECOPT_DODEFAULT, IntPtr.Zero, IntPtr.Zero)
            == VSConstants.S_OK;
    }
}
