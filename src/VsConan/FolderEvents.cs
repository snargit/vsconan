using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsConan;

/// <summary>Forwards Open Folder open/close notifications; the IVsSolutionEvents members are unused.</summary>
internal sealed class FolderEvents : IVsSolutionEvents, IVsSolutionEvents7
{
    private readonly Action<string> _afterOpenFolder;
    private readonly Action<string> _beforeCloseFolder;

    public FolderEvents(Action<string> afterOpenFolder, Action<string> beforeCloseFolder)
    {
        _afterOpenFolder = afterOpenFolder;
        _beforeCloseFolder = beforeCloseFolder;
    }

    public void OnAfterOpenFolder(string folderPath) => _afterOpenFolder(folderPath);

    public void OnBeforeCloseFolder(string folderPath) => _beforeCloseFolder(folderPath);

    public void OnQueryCloseFolder(string folderPath, ref int pfCancel) { }

    public void OnAfterCloseFolder(string folderPath) { }

    public void OnAfterLoadAllDeferredProjects() { }

    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;

    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;

    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;

    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;

    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;

    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;

    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution) => VSConstants.S_OK;

    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

    public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

    public int OnAfterCloseSolution(object pUnkReserved) => VSConstants.S_OK;
}
