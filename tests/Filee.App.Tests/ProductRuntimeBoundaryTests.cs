using System.Reflection;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Core.Watching;
using Filee.Core.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class ProductRuntimeBoundaryTests
{
    [AvaloniaFact]
    public async Task Browser_drops_are_disposed_without_opening_streams_or_starting_jobs()
    {
        await using var f = new Fixture();
        var item = new ProbeWebItem();
        var web = new WebDrop([item], f.Directory);

        Assert.Null(f.Service.Start(web.Placeholders, new Preset { TargetFormat = "pdf" }, web: web));
        Assert.True(item.Disposed);
        Assert.Equal(0, item.Saves);
        Assert.Empty(f.Service.Jobs);
        Assert.Equal(0, f.Engine.Calls);
    }

    [AvaloniaFact]
    public void Browser_urls_and_virtual_files_never_become_conversion_inputs()
    {
        TestServices.EnsureInitialized();
        using var url = new DataTransfer();
        url.Add(DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat(BrowserDragFormats.NetscapeUrl),
            Encoding.UTF8.GetBytes("https://example.invalid/report.pdf\nReport\0")));
        Assert.False(DraggedFiles.Offered(url));
        Assert.Same(DraggedItems.None, DraggedFiles.Read(url, forDrop: true));

        var storage = DispatchProxy.Create<IStorageFile, VirtualStorageFile>();
        var probe = (VirtualStorageFile)(object)storage;
        using var virtualDrop = new DataTransfer();
        virtualDrop.Add(DataTransferItem.CreateFile(storage));
        Assert.Same(DraggedItems.None, DraggedFiles.Read(virtualDrop, forDrop: false));
        Assert.False(probe.Disposed);
        Assert.Same(DraggedItems.None, DraggedFiles.Read(virtualDrop, forDrop: true));
        Assert.True(probe.Disposed);
        Assert.Equal(0, probe.Opens);
    }

    [AvaloniaFact]
    public async Task Invalid_sources_targets_and_zip_output_never_start_a_partial_batch()
    {
        await using var f = new Fixture();
        var text = f.Write("notes.txt");
        var script = f.Write("script.py");
        var pdf = new Preset { TargetFormat = "pdf" };
        Assert.Null(f.Service.Start([text, script], pdf));
        Assert.Null(f.Service.Start([text, f.Directory], pdf));
        Assert.Null(f.Service.Start([Path.Combine(f.Directory, "missing.txt")], pdf));
        Assert.Null(f.Service.Start([text], new Preset { TargetFormat = "png" }));
        Assert.Null(f.Service.Start([text], new Preset { TargetFormat = "pdf", Kind = PresetKind.Tool }));
        Assert.Null(f.Service.Start([text], new Preset { TargetFormat = "pdf", Output = { ZipResult = true } }));
        Assert.Empty(f.Service.Jobs);
        Assert.Equal(0, f.Engine.Calls);
    }

    [AvaloniaFact]
    public async Task Valid_local_documents_still_convert_and_disposed_services_stop_accepting_work()
    {
        await using var f = new Fixture();
        var text = f.Write("notes.txt");
        var finished = new TaskCompletionSource<ConversionJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Queue.JobFinished += (_, job) => finished.TrySetResult(job);
        Assert.NotNull(f.Service.Start([text], new Preset { TargetFormat = "pdf" }));
        var completed = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(JobState.Completed, completed.State);
        Assert.Single(completed.Outputs);
        Assert.Equal("original", File.ReadAllText(text));
        f.Service.Dispose();
        Assert.Null(f.Service.Start([text], new Preset { TargetFormat = "pdf" }));
        Assert.Equal(1, f.Engine.Calls);
    }

    [AvaloniaFact]
    public async Task Disposal_between_starting_a_job_and_registering_its_wait_cancels_the_caller()
    {
        await using var f = new Fixture(holdConversion: true);
        var text = f.Write("notes.txt");
        var gapReached = false;
        f.Service.BeforeWaitRegistration = () =>
        {
            gapReached = true;
            var job = Assert.Single(f.Service.Jobs).Job;
            Assert.True(job.State is JobState.Queued or JobState.Running);
            f.Service.Dispose();
        };

        var run = f.Service.RunAsync([text], new Preset { TargetFormat = "pdf" });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(gapReached);
    }

    [AvaloniaFact]
    public async Task Watchers_stop_after_preset_removal_and_cannot_restart_after_disposal()
    {
        await using var f = new Fixture();
        await using var watches = new WatchFolderService(f.Store, f.Service, NullLogger<WatchFolderService>.Instance);
        var rule = new WatchRule { Folder = f.Directory, PresetId = "to-pdf" };
        f.Store.Settings.WatchFolders.Add(rule);
        watches.Start();
        watches.Start();
        await watches.ApplyAsync();
        Assert.True(watches.IsWatching(rule.Id));
        Assert.NotNull(f.Service.WatchRetry);

        f.Store.Presets.RemoveAll(p => p.Id == "to-pdf");
        f.Store.SaveLibrary();
        await watches.ApplyAsync();
        Assert.False(watches.IsWatching(rule.Id));

        f.Store.Presets.Add(new Preset { Id = "to-pdf", TargetFormat = "pdf" });
        f.Store.SaveLibrary(); // Schedules a delayed apply; disposal must cancel it.
        await watches.DisposeAsync();
        f.Store.SaveSettings();
        watches.Start();
        await watches.ApplyAsync();
        Assert.False(watches.IsWatching(rule.Id));
        Assert.Null(f.Service.WatchRetry);
        Assert.Empty(f.Service.Jobs);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("convertdesk-runtime-").FullName;
        public UserDataStore Store { get; }
        public TestEngine Engine { get; }
        public JobQueue Queue { get; }
        public ConversionService Service { get; }

        public Fixture(bool holdConversion = false)
        {
            Store = new UserDataStore(Path.Combine(Directory, "settings"));
            Store.Load();
            Engine = new TestEngine(holdConversion);
            Queue = new JobQueue(new ConverterCatalog([Engine]), enforceProductFormats: true);
            Service = new ConversionService(Queue, Store, new KeyLocalizer(), new NullPlatformServices());
        }
        public string Write(string name)
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, "original");
            return path;
        }
        public async ValueTask DisposeAsync()
        {
            Engine.Continue.TrySetResult();
            await Queue.DisposeAsync();
            Dispatcher.UIThread.RunJobs();
            Service.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class ProbeWebItem() : WebDropItem("remote.txt")
    {
        public int Saves;
        public bool Disposed;
        public override Task<string> SaveAsync(string folder, WebDownloader downloader, IProgress<TransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            Saves++;
            throw new InvalidOperationException("A browser transfer must never be fetched.");
        }
        public override void Dispose() => Disposed = true;
    }

    public class VirtualStorageFile : DispatchProxy
    {
        public bool Disposed;
        public int Opens;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Name" => "remote.pdf",
            "get_Path" => new Uri("virtual-file:///remote.pdf"),
            "get_CanBookmark" => false,
            "OpenReadAsync" => Open(),
            "Dispose" => DisposeItem(),
            _ => throw new NotSupportedException(method?.Name),
        };
        private Task<Stream> Open()
        {
            Opens++;
            throw new InvalidOperationException("Virtual document streams must not be opened.");
        }
        private object? DisposeItem() { Disposed = true; return null; }
    }

    private sealed class TestEngine(bool holdConversion) : IConverter
    {
        public int Calls;
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "test-local";
        public string DisplayName => "Local test engine";
        public IReadOnlyList<ConversionEdge> Edges { get; } = [new("txt", "pdf")];
        public int MaxParallelism => 1;
        public EngineStatus GetStatus() => EngineStatus.Available();
        public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            if (holdConversion)
                await Continue.Task.WaitAsync(cancellationToken);
            var path = step.Output.Allocate(step.To)!;
            File.WriteAllText(path, "converted");
            return [path];
        }
    }

    private sealed class KeyLocalizer : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key;
        public string Format(string key, params object[] args) => key;
        public event EventHandler? LanguageChanged { add { } remove { } }
    }
}
