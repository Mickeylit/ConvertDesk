// UI-facing wrapper around JobQueue: starts jobs, keeps bindable lists for the toast window and records history.
// Jobs started from the donut are first shown by its progress ring and only appear in the toast when the ring
// hands them over (long job, failure, or a new drag).

using System.Collections.ObjectModel;
using Avalonia.Threading;
using Filee.App.ViewModels;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Core.Web;

namespace Filee.App.Services;

public sealed class ConversionService : IDisposable
{
    private static readonly TimeSpan KeepSuccess = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepErrors = TimeSpan.FromSeconds(15);

    private readonly JobQueue _queue;
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly object _lifecycleGate = new();
    private volatile bool _disposed;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JobViewModel> _byId = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<ConversionJob>> _waiting = new();

    /// <summary>Jobs that came from a watch folder: job id → rule id (for the history and for retrying).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _watchJobs = new();

    public ConversionService(JobQueue queue, UserDataStore store, ILocalizer loc, IPlatformServices platform)
    {
        _queue = queue;
        _store = store;
        _loc = loc;
        _platform = platform;
        _queue.JobUpdated += OnJobUpdated;
        _queue.JobFinished += OnJobFinished;
    }

    /// <summary>All jobs that are running or finished recently, newest first. UI thread only.</summary>
    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    /// <summary>The subset shown in the toast window. UI thread only.</summary>
    public ObservableCollection<JobViewModel> ToastJobs { get; } = [];

    /// <summary>Starts converting files with a preset.</summary>
    /// <param name="files">Source files.</param>
    /// <param name="preset">Preset to apply.</param>
    /// <param name="showInToast">False when another view (the donut's progress ring) displays the job first.</param>
    /// <param name="web">Legacy browser drops are rejected and disposed without fetching anything.</param>
    /// <returns>The job's view model, or null when there was nothing to convert.</returns>
    public JobViewModel? Start(IReadOnlyList<string> files, Preset preset, bool showInToast = true, WebDrop? web = null)
    {
        if (_disposed || web is not null || !AreSupportedLocalFiles(files) || !IsSupportedPreset(preset))
        {
            web?.Dispose();
            return null;
        }
        // A preset on "Default" saves where Settings > General says (read now, so a changed default applies at once).
        preset = _store.Settings.WithDefaultOutput(preset);
        if (!IsSupportedPreset(preset))
            return null;
        var job = _queue.Enqueue(files, preset, _loc.DisplayName(preset));
        var vm = new JobViewModel(job, _loc, _platform, Remove, RetryFailed);
        _byId[job.Id] = vm;
        Jobs.Insert(0, vm);
        if (showInToast)
            ToastJobs.Insert(0, vm);
        return vm;
    }

    /// <summary>
    /// Converts a watch folder's files again (rule id, files, preset as the job ran it): WatchFolderService sets it, so
    /// a retry also does what the folder does afterwards (moving originals).
    /// </summary>
    public Func<string, IReadOnlyList<string>, Preset, Task>? WatchRetry { get; set; }

    // Tests can stop at the UI-start/background-wait boundary to exercise shutdown deterministically.
    internal Action? BeforeWaitRegistration { get; set; }

    /// <summary>
    /// Converts files that failed again with the preset their job ran with (shown in the toast). Files that are no
    /// longer there are left out. Files of a watch folder go through <see cref="WatchRetry"/>.
    /// </summary>
    /// <returns>The new job, or null when none of the files is left or the watch folder converts them.</returns>
    public JobViewModel? Retry(IEnumerable<string> sources, Preset preset, string? watchRuleId = null)
    {
        var files = sources.Where(Exists).Distinct(FileSystemPaths.Comparer).ToList();
        if (_disposed || !AreSupportedLocalFiles(files) || !IsSupportedPreset(preset))
            return null;
        // History keeps no PDF password: it comes from the preset when that still has one (else the job refuses).
        if (preset.Pdf.PasswordLeftOut && _store.FindPreset(preset.Id)?.Pdf.Password is { Length: > 0 } password)
        {
            preset = preset.Clone();
            preset.Pdf.Password = password;
            preset.Pdf.PasswordLeftOut = false;
        }
        if (watchRuleId is not null && WatchRetry is { } watchRetry)
        {
            _ = watchRetry(watchRuleId, files, preset);
            return null;
        }
        return Start(files, preset);
    }

    /// <summary>Checks the complete batch before any conversion or output allocation can begin.</summary>
    internal static bool AreSupportedLocalFiles(IReadOnlyList<string> files) =>
        files.Count > 0 && files.All(path => ProductFormatPolicy.IsAllowedPath(path)
            && !WebDrop.IsPlaceholder(path) && File.Exists(path));

    private static bool IsSupportedPreset(Preset preset) =>
        !preset.IsGroup && !preset.IsTool && !preset.Output.ZipResult
        && ProductFormatPolicy.IsAllowedFormatId(preset.TargetFormat);

    /// <summary>True when a source file (or folder) is still where it was.</summary>
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private void RetryFailed(JobViewModel vm)
    {
        var watchRuleId = _watchJobs.GetValueOrDefault(vm.Job.Id);
        var failed = vm.FailedSources.ToList();
        Remove(vm);
        Retry(failed, vm.Job.Preset, watchRuleId);
    }

    /// <summary>
    /// Converts like <see cref="Start"/> (shown in the toast and history) and completes when the job has finished.
    /// Callable from any thread; used by watch folders.
    /// </summary>
    /// <param name="watchRuleId">The watch folder the files come from; recorded in the history.</param>
    public async Task<ConversionJob?> RunAsync(IReadOnlyList<string> files, Preset preset, string? watchRuleId = null)
    {
        var vm = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var started = Start(files, preset);
            if (started is not null && watchRuleId is not null)
                _watchJobs[started.Job.Id] = watchRuleId;
            return started;
        });
        if (vm is null)
            return null;
        BeforeWaitRegistration?.Invoke();
        TaskCompletionSource<ConversionJob> finished;
        lock (_lifecycleGate)
        {
            // Dispose may run after the UI started the job but before this continuation resumes.
            // Register under the same lock that closes the service so no waiter can miss cancellation.
            if (_disposed)
                throw new OperationCanceledException("The conversion service was disposed.");
            finished = _waiting.GetOrAdd(vm.Job.Id, _ => new TaskCompletionSource<ConversionJob>(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        // The job may have finished before the wait was registered.
        if (vm.Job.State is not (JobState.Queued or JobState.Running))
            finished.TrySetResult(vm.Job);
        try
        {
            return await finished.Task;
        }
        finally
        {
            _waiting.TryRemove(vm.Job.Id, out _);
        }
    }

    /// <summary>Moves a job into the toast window (no-op if it is already there or gone).</summary>
    public void ShowInToast(JobViewModel vm)
    {
        if (Jobs.Contains(vm) && !ToastJobs.Contains(vm))
            ToastJobs.Insert(0, vm);
    }

    private void OnJobUpdated(object? sender, ConversionJob job)
    {
        if (_disposed)
            return;
        // Called from worker threads, possibly many times per second: coalesce into one UI update.
        if (_byId.TryGetValue(job.Id, out var vm) && vm.TryMarkPending())
            Dispatcher.UIThread.Post(vm.Refresh, DispatcherPriority.Background);
    }

    private void OnJobFinished(object? sender, ConversionJob job)
    {
        if (_disposed)
            return;
        var entry = HistoryEntry.From(job);
        entry.WatchRuleId = _watchJobs.GetValueOrDefault(job.Id);
        _store.AddHistory(entry);
        if (_waiting.TryGetValue(job.Id, out var waiting))
            waiting.TrySetResult(job);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !_byId.TryGetValue(job.Id, out var vm))
                return;
            vm.Refresh();
            var keep = vm.HasErrors ? KeepErrors : KeepSuccess;
            DispatcherTimer.RunOnce(() => Remove(vm), keep);
        });
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var waiting in _waiting.Values)
                waiting.TrySetCanceled();
            _waiting.Clear();
        }
        _queue.JobUpdated -= OnJobUpdated;
        _queue.JobFinished -= OnJobFinished;
        WatchRetry = null;
    }

    private void Remove(JobViewModel vm)
    {
        Jobs.Remove(vm);
        ToastJobs.Remove(vm);
        _byId.TryRemove(vm.Job.Id, out _);
        _watchJobs.TryRemove(vm.Job.Id, out _);
    }
}
