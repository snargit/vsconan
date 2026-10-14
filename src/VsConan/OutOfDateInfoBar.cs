using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VsConan;

/// <summary>
/// Info bar shown when a watched Conan input changed and the install is out of date: offers to
/// reinstall now, or always (sets autoInstallOnChange in the project's VsConan settings).
/// </summary>
internal sealed class OutOfDateInfoBar : IVsInfoBarUIEvents
{
    private const string InstallNow = "install";
    private const string AlwaysInstall = "always";

    private readonly Action _onInstallNow;
    private readonly Action _onAlwaysInstall;
    private readonly Action _onClosed;
    private uint _cookie;

    private OutOfDateInfoBar(Action onInstallNow, Action onAlwaysInstall, Action onClosed)
    {
        _onInstallNow = onInstallNow;
        _onAlwaysInstall = onAlwaysInstall;
        _onClosed = onClosed;
    }

    /// <returns>False if the info bar couldn't be shown; <paramref name="onClosed"/> is then not called.</returns>
    public static async System.Threading.Tasks.Task<bool> ShowAsync(
        AsyncPackage package, string message, Action onInstallNow, Action onAlwaysInstall, Action onClosed)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (await package.GetServiceAsync(typeof(SVsShell)) is not IVsShell shell
            || await package.GetServiceAsync(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory
            || ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var hostObject))
            || hostObject is not IVsInfoBarHost host)
        {
            return false;
        }

        var model = new InfoBarModel(
            new[] { new InfoBarTextSpan(message + " ") },
            new IVsInfoBarActionItem[]
            {
                new InfoBarHyperlink("Install now", InstallNow),
                new InfoBarHyperlink("Always install automatically", AlwaysInstall),
            },
            KnownMonikers.StatusWarning,
            isCloseButtonVisible: true);

        var element = factory.CreateInfoBar(model);
        var events = new OutOfDateInfoBar(onInstallNow, onAlwaysInstall, onClosed);
        element.Advise(events, out events._cookie);
        host.AddInfoBar(element);
        return true;
    }

    public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        infoBarUIElement.Close();

        if (actionItem.ActionContext as string == AlwaysInstall)
        {
            _onAlwaysInstall();
        }
        else
        {
            _onInstallNow();
        }
    }

    public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        infoBarUIElement.Unadvise(_cookie);
        _onClosed();
    }
}
