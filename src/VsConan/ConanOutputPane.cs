using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VsConan;

/// <summary>The "Conan" pane in the Output window.</summary>
internal sealed class ConanOutputPane
{
    private static readonly Guid PaneGuid = new("02eb4364-aad9-40e7-9549-3d3048ebab39");

    private readonly IVsOutputWindowPane _pane;

    private ConanOutputPane(IVsOutputWindowPane pane)
    {
        _pane = pane;
    }

    public static async System.Threading.Tasks.Task<ConanOutputPane> CreateAsync(AsyncPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var outputWindow = (IVsOutputWindow?)await package.GetServiceAsync(typeof(SVsOutputWindow))
            ?? throw new InvalidOperationException("SVsOutputWindow is unavailable.");

        var guid = PaneGuid;
        outputWindow.CreatePane(ref guid, "Conan", fInitVisible: 1, fClearWithSolution: 0);
        outputWindow.GetPane(ref guid, out var pane);
        return new ConanOutputPane(pane);
    }

    /// <summary>Safe to call from any thread.</summary>
    public void WriteLine(string text)
    {
#pragma warning disable VSTHRD010 // OutputStringThreadSafe is documented as callable from any thread.
        _pane.OutputStringThreadSafe(text + Environment.NewLine);
#pragma warning restore VSTHRD010
    }

    public async Task ActivateAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        _pane.Activate();
    }
}
