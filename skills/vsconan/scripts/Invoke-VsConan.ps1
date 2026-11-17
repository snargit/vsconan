<#
.SYNOPSIS
Runs a VsConan automation command in the Visual Studio that has a folder open, waits for it, and
prints the result JSON.

.DESCRIPTION
Finds the Visual Studio instance (2022 or later) whose open folder (File > Open > Folder) is
-Folder, by its entry in the Running Object Table, so it works with several instances open. Runs
Conan.<Command> with a request id, retries while VsConan is loading or Visual Studio is busy,
then waits for .vs\VsConan\command-results\<id>.json to leave "running" and writes that JSON to
standard output.

Exit codes: 0 the command succeeded, 1 it failed (the JSON's "error" says why), 2 it couldn't be
run (the reason is written to standard error).

Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
.\Invoke-VsConan.ps1 ShowStatus -Folder C:\src\app

.EXAMPLE
.\Invoke-VsConan.ps1 SelectProfiles -HostProfiles default,debug -BuildProfile default

.EXAMPLE
.\Invoke-VsConan.ps1 InstallDependencies
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('ShowStatus', 'SelectProfiles', 'InstallDependencies')]
    [string] $Command,

    # The folder open in Visual Studio; default: the current directory.
    [string] $Folder = (Get-Location).Path,

    # SelectProfiles: the host profiles to install for.
    [string[]] $HostProfiles = @(),

    # SelectProfiles: the build profile; omit to keep the current one.
    [string] $BuildProfile,

    # InstallDependencies: reinstall even if nothing has changed.
    [switch] $Force,

    # Most seconds to wait for the command to finish (installs that build packages take long).
    [int] $TimeoutSeconds = 1800
)

$ErrorActionPreference = 'Stop'

function Fail([string] $message) {
    [Console]::Error.WriteLine($message)
    exit 2
}

function Normalize([string] $path) {
    [IO.Path]::GetFullPath($path).TrimEnd('\', '/')
}

# Visual Studio rejects COM calls while it's busy (RPC_E_CALL_REJECTED, RPC_E_SERVERCALL_RETRYLATER).
function Test-Busy($exception) {
    $e = $exception
    while ($e) {
        if ($e -is [Runtime.InteropServices.COMException] -and
            ($e.HResult -eq -2147418111 -or $e.HResult -eq -2147417846)) {
            return $true
        }
        $e = $e.InnerException
    }
    return $false
}

function Invoke-Retrying([scriptblock] $call, [int] $seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ($true) {
        try {
            return & $call
        }
        catch {
            if (-not (Test-Busy $_.Exception) -or (Get-Date) -gt $deadline) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

if (-not ('VsConanRot' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class VsConanRot
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);

    /// <summary>The DTE objects of the running Visual Studio instances.</summary>
    public static List<object> VisualStudioInstances()
    {
        var instances = new List<object>();
        IRunningObjectTable table;
        if (GetRunningObjectTable(0, out table) != 0) return instances;
        IEnumMoniker monikers;
        table.EnumRunning(out monikers);
        var moniker = new IMoniker[1];
        while (monikers.Next(1, moniker, IntPtr.Zero) == 0)
        {
            string name;
            try
            {
                IBindCtx context;
                CreateBindCtx(0, out context);
                moniker[0].GetDisplayName(context, null, out name);
            }
            catch (COMException) { continue; }
            if (!name.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal)) continue;
            object dte;
            if (table.GetObject(moniker[0], out dte) == 0) instances.Add(dte);
        }
        return instances;
    }
}
'@
}

$target = Normalize $Folder

# The instance with the folder open. In Open Folder mode, Solution.FullName is the folder.
$dte = $null
foreach ($instance in [VsConanRot]::VisualStudioInstances()) {
    try {
        $open = Invoke-Retrying { $instance.Solution.FullName } 5
    }
    catch {
        continue
    }
    if ($open -and (Normalize $open) -ieq $target) {
        $dte = $instance
        break
    }
}
if (-not $dte) {
    Fail "No Visual Studio has $target open as a folder (File > Open > Folder)."
}

$name = "Conan.$Command"
try {
    Invoke-Retrying { $null = $dte.Commands.Item($name) } | Out-Null
}
catch {
    Fail "VsConan isn't installed in the Visual Studio that has $target open: it has no $name command."
}

$arguments = @()
if ($Command -eq 'SelectProfiles') {
    $profiles = @($HostProfiles | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($profiles.Count -eq 0) {
        Fail 'Pass -HostProfiles (ShowStatus lists them in availableProfiles).'
    }
    $arguments += $profiles
    if ($BuildProfile) { $arguments += "/build:$($BuildProfile.Trim())" }
    $bad = $arguments | Where-Object { $_ -match '\s' }
    if ($bad) { Fail "Not a profile name: '$($bad | Select-Object -First 1)'." }
}
if ($Command -eq 'InstallDependencies' -and $Force) {
    $arguments += '/force'
}
$id = [guid]::NewGuid().ToString('N')
$arguments += "/id:$id"

# Right after a folder opens VsConan may not have loaded, and a modal dialog blocks commands:
# both make the command unavailable for a while.
$deadline = (Get-Date).AddSeconds(20)
while ($true) {
    try {
        Invoke-Retrying { $dte.ExecuteCommand($name, ($arguments -join ' ')) }
        break
    }
    catch {
        if ((Get-Date) -gt $deadline) {
            $managed = (Test-Path (Join-Path $target 'CMakeLists.txt')) -and
                ((Test-Path (Join-Path $target 'conanfile.py')) -or (Test-Path (Join-Path $target 'conanfile.txt')))
            if (-not $managed) {
                Fail "VsConan doesn't manage ${target}: its root needs a conanfile.py or conanfile.txt and a CMakeLists.txt."
            }
            Fail "$name couldn't run (VsConan is still loading, or a dialog is open in Visual Studio): $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 1
    }
}

# The request's own result file; the shared one (most recent command) if this VsConan has none.
$own = Join-Path $target ".vs\VsConan\command-results\$id.json"
$shared = Join-Path $target '.vs\VsConan\command-result.json'
$deadline = (Get-Date).AddSeconds([Math]::Max(10, $TimeoutSeconds))
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    foreach ($path in @($own, $shared)) {
        try {
            if (-not (Test-Path -LiteralPath $path)) { continue }
            $text = [IO.File]::ReadAllText($path)
            $result = $text | ConvertFrom-Json
        }
        catch {
            continue # Being written: read it again next time.
        }
        if ($result.id -ne $id -or $result.state -eq 'running') { continue }
        Write-Output $text
        if ($result.state -eq 'succeeded') { exit 0 } else { exit 1 }
    }
}
Fail "$name is still running after $TimeoutSeconds s (see the Conan output pane). ShowStatus reports installing until it finishes."
