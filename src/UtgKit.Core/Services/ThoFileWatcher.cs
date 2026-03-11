using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UtgKit.Core.Services;

/// <summary>
/// Watches the THO repository directory for file changes (.xml / .json) and
/// incrementally updates the <see cref="ThoFileService"/> in-memory index.
///
/// <para>
/// <strong>Debouncing:</strong> Rapid bursts of file-system events (common when
/// editors save with temp-file renames or when git operations touch many files)
/// are collapsed into a single re-index per file using a short delay window.
/// </para>
///
/// <para>
/// <strong>Import safety:</strong> When <see cref="ThoFileService.IsWatcherSuppressed"/>
/// is true the watcher records changed paths but defers processing until
/// suppression ends, avoiding conflicts with import-service writes.
/// </para>
/// </summary>
public sealed class ThoFileWatcher : BackgroundService
{
    private readonly ThoFileService _thoFileService;
    private readonly ThoRepoSettings _settings;
    private readonly ILogger<ThoFileWatcher> _logger;

    /// <summary>
    /// How long to wait after the last event for a given path before processing it.
    /// </summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How often the background loop checks for pending work and suppression state.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Pending file paths with the UTC tick time of their most recent event.
    /// Access is synchronized via <see cref="_pendingLock"/>.
    /// </summary>
    private readonly Dictionary<string, long> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingLock = new();

    public ThoFileWatcher(
        ThoFileService thoFileService,
        IOptions<ThoRepoSettings> settings,
        ILogger<ThoFileWatcher> logger)
    {
        _thoFileService = thoFileService;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.Path) || !Directory.Exists(_settings.Path))
        {
            _logger.LogWarning("THO repo path is not configured or does not exist; file watcher disabled.");
            return;
        }

        _logger.LogInformation("Starting file watcher on {Path}", _settings.Path);

        using var xmlWatcher = CreateWatcher("*.xml");
        using var jsonWatcher = CreateWatcher("*.json");

        // Process loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TickInterval, stoppingToken);
            ProcessPendingChanges();
        }
    }

    private FileSystemWatcher CreateWatcher(string filter)
    {
        var watcher = new FileSystemWatcher(_settings.Path)
        {
            Filter = filter,
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size
                         | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        watcher.Deleted += OnFileDeleted;
        watcher.Error += OnWatcherError;

        return watcher;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        EnqueueChange(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        // Treat rename as delete-old + change-new
        EnqueueDelete(e.OldFullPath);
        EnqueueChange(e.FullPath);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        EnqueueDelete(e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "FileSystemWatcher error; some changes may be missed.");
    }

    private void EnqueueChange(string fullPath)
    {
        lock (_pendingLock)
        {
            _pendingDeletes.Remove(fullPath);
            _pendingChanges[fullPath] = Environment.TickCount64;
        }
    }

    private void EnqueueDelete(string fullPath)
    {
        lock (_pendingLock)
        {
            _pendingChanges.Remove(fullPath);
            _pendingDeletes.Add(fullPath);
        }
    }

    private void ProcessPendingChanges()
    {
        // While suppression is active, leave events queued
        if (_thoFileService.IsWatcherSuppressed)
            return;

        var now = Environment.TickCount64;
        List<string>? readyChanges = null;
        List<string>? readyDeletes = null;

        lock (_pendingLock)
        {
            // Collect changes whose debounce window has elapsed
            foreach (var (path, tick) in _pendingChanges)
            {
                if (now - tick >= DebounceDelay.TotalMilliseconds)
                {
                    readyChanges ??= [];
                    readyChanges.Add(path);
                }
            }

            if (readyChanges is not null)
            {
                foreach (var p in readyChanges)
                    _pendingChanges.Remove(p);
            }

            // Deletes don't need debouncing — process immediately
            if (_pendingDeletes.Count > 0)
            {
                readyDeletes = [.. _pendingDeletes];
                _pendingDeletes.Clear();
            }
        }

        if (readyChanges is not null)
        {
            foreach (var path in readyChanges)
            {
                try
                {
                    _thoFileService.ReindexFile(path);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to reindex changed file: {Path}", path);
                }
            }
        }

        if (readyDeletes is not null)
        {
            foreach (var path in readyDeletes)
            {
                try
                {
                    _thoFileService.RemoveFileFromIndex(path);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to remove deleted file from index: {Path}", path);
                }
            }
        }
    }
}
