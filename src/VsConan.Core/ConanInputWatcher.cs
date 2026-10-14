using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VsConan.Core;

/// <summary>
/// Watches a project's Conan inputs while its folder is open: the conanfile, conan.lock,
/// conandata.yml and .conanrc in the project folder, plus the profiles in the project's Conan home.
/// A burst of changes is reported once, <see cref="DebounceInterval"/> after the last. A report
/// only means "check": the receiver compares fingerprints, so saves that change nothing are ignored there.
/// </summary>
public sealed class ConanInputWatcher : IDisposable
{
    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> WatchedNames = new(
        InstallFingerprint.InputFileNames.Concat(new[] { ".conanrc" }), StringComparer.OrdinalIgnoreCase);

    private readonly string _projectDirectory;
    private readonly Func<CancellationToken, Task<string?>> _resolveConanHome;
    private readonly Action<IReadOnlyList<string>> _onChange;
    private readonly FileSystemWatcher _inputs;
    private readonly Timer _timer;
    private readonly HashSet<string> _reasons = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _disposal = new();
    private FileSystemWatcher? _profiles;
    private string? _profilesFolder;

    /// <param name="resolveConanHome">The project's Conan home ('conan config home' run in the project folder), or null.</param>
    /// <param name="onChange">Called on a thread-pool thread with what changed, e.g. "conanfile.txt changed".</param>
    public ConanInputWatcher(
        string projectDirectory,
        Func<CancellationToken, Task<string?>> resolveConanHome,
        Action<IReadOnlyList<string>> onChange)
    {
        _projectDirectory = projectDirectory;
        _resolveConanHome = resolveConanHome;
        _onChange = onChange;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        _inputs = CreateWatcher(projectDirectory, includeSubdirectories: false, path =>
        {
            var name = Path.GetFileName(path);
            if (!WatchedNames.Contains(name))
            {
                return;
            }

            if (name.Equals(".conanrc", StringComparison.OrdinalIgnoreCase))
            {
                // The Conan home, and so the profiles to watch, may have moved.
                _ = WatchProfilesAsync();
            }

            Schedule($"{name} changed");
        });
    }

    public TimeSpan DebounceInterval { get; set; } = DefaultDebounceInterval;

    /// <summary>The profiles folder being watched, once <see cref="StartAsync"/> has resolved it.</summary>
    public string? ProfilesFolder => _profilesFolder;

    public Task StartAsync() => WatchProfilesAsync();

    /// <summary>(Re)creates the watcher on &lt;Conan home&gt;/profiles, following the project's current Conan home.</summary>
    private async Task WatchProfilesAsync()
    {
        string? home;
        try
        {
            home = await _resolveConanHome(_disposal.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var folder = home is null ? null : Path.Combine(home, "profiles");
        lock (_lock)
        {
            if (_disposal.IsCancellationRequested || string.Equals(folder, _profilesFolder, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _profiles?.Dispose();
            _profiles = null;
            _profilesFolder = folder;
            if (folder is null || !Directory.Exists(folder))
            {
                return;
            }

            _profiles = CreateWatcher(folder, includeSubdirectories: true, path =>
            {
                var name = path.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                Schedule($"Conan profile '{name}' changed");
            });
        }
    }

    private void Schedule(string reason)
    {
        lock (_lock)
        {
            if (_disposal.IsCancellationRequested)
            {
                return;
            }

            _reasons.Add(reason);
            _timer.Change(DebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        List<string> reasons;
        lock (_lock)
        {
            if (_disposal.IsCancellationRequested || _reasons.Count == 0)
            {
                return;
            }

            reasons = _reasons.ToList();
            _reasons.Clear();
        }

        _onChange(reasons);
    }

    private static FileSystemWatcher CreateWatcher(string folder, bool includeSubdirectories, Action<string> onPath)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += (_, e) => onPath(e.FullPath);
        watcher.Created += (_, e) => onPath(e.FullPath);
        watcher.Deleted += (_, e) => onPath(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            onPath(e.OldFullPath);
            onPath(e.FullPath);
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposal.IsCancellationRequested)
            {
                return;
            }

            _disposal.Cancel();
            _timer.Dispose();
            _inputs.Dispose();
            _profiles?.Dispose();
        }
    }
}
