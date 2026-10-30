using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.Services.Documents;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Core.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public sealed class DocumentShellLifecycleTests
{
    [AvaloniaFact]
    public async Task Desktop_shutdown_cancels_the_active_engine_and_never_starts_the_queued_job()
    {
        await using var fixture = new ShutdownFixture(cooperative: true);
        var active = fixture.Conversions.Start([fixture.Input], fixture.Preset)!.Job;
        await fixture.Converter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var queued = fixture.Conversions.Start([fixture.Input], fixture.Preset)!.Job;
        Assert.Equal(JobState.Queued, queued.State);

        // Intentionally synchronous on the Avalonia thread: a captured UI continuation would time out here.
        Assert.True(DocumentShutdown.Stop(fixture.Conversions, fixture.Queue));
        Assert.True(fixture.Converter.Cancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(JobState.Cancelled, active.State);
        Assert.Equal(JobState.Cancelled, queued.State);
        Assert.Equal(1, fixture.Converter.Calls);
        Assert.False(fixture.Queue.IsBusy);
        Assert.Null(fixture.Conversions.Start([fixture.Input], fixture.Preset));
    }

    [AvaloniaFact]
    public async Task Desktop_shutdown_returns_within_its_bound_when_an_engine_ignores_cancellation()
    {
        await using var fixture = new ShutdownFixture(cooperative: false);
        var active = fixture.Conversions.Start([fixture.Input], fixture.Preset)!.Job;
        await fixture.Converter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var queued = fixture.Conversions.Start([fixture.Input], fixture.Preset)!.Job;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(DocumentShutdown.Stop(fixture.Conversions, fixture.Queue, TimeSpan.FromMilliseconds(150)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Shutdown blocked for {watch.Elapsed}.");
        Assert.True(active.CancellationToken.IsCancellationRequested);
        Assert.True(queued.CancellationToken.IsCancellationRequested);
        Assert.Null(fixture.Conversions.Start([fixture.Input], fixture.Preset));

        fixture.Converter.Release.TrySetResult();
        await fixture.Queue.DisposeAsync();
        Assert.Equal(JobState.Cancelled, queued.State);
        Assert.Equal(1, fixture.Converter.Calls);
    }

    [AvaloniaFact]
    public void Production_navigation_cannot_open_removed_general_purpose_pages()
    {
        TestServices.EnsureInitialized("en");
        using var shell = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>());
        Assert.Equal(["home", "general", "triggers", "theme", "engines", "about"], shell.Items.Select(i => i.Key));
        var home = shell.CurrentPage;
        foreach (var obsolete in new[] { "watch", "toolbar", "presets", "not-a-page" })
        {
            shell.Navigate(obsolete);
            Assert.Same(home, shell.CurrentPage);
        }
        shell.Navigate("about");
        Assert.IsType<DocumentAboutPageViewModel>(shell.CurrentPage);
        shell.GoHomeCommand.Execute(null);
        Assert.IsType<HomePageViewModel>(shell.CurrentPage);
        Assert.False(shell.SettingsOpen);
    }

    [AvaloniaFact]
    public void Document_composition_does_not_register_unused_download_watch_or_spreadsheet_services()
    {
        TestServices.EnsureInitialized("en");
        Assert.Null(AppHost.Services.GetService<WebDownloader>());
        Assert.Null(AppHost.Services.GetService<WatchFolderService>());
        Assert.Null(AppHost.Services.GetService<SpreadsheetPrintService>());
        Assert.NotNull(AppHost.Get<ConversionService>());
    }

    [AvaloniaFact]
    public void Recreating_and_disposing_the_tray_removes_its_icon_and_detaches_settings_updates()
    {
        TestServices.EnsureInitialized("en");
        var directory = Directory.CreateTempSubdirectory("convertdesk-tray-").FullName;
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            var app = Application.Current!;
            var tray = new TrayService(store, AppHost.Get<ILocalizer>(), new IconPlatform(), AppHost.Get<WindowService>());
            tray.Create(app);
            tray.Create(app);
            var icon = Assert.Single(TrayIcon.GetIcons(app)!);
            Assert.Equal(4, icon.Menu!.Items.Count);
            tray.Dispose();
            tray.Dispose();
            Assert.DoesNotContain(icon, TrayIcon.GetIcons(app)!);
            store.Settings.ShowMenuBarIcon = false;
            store.SaveSettings();
            Assert.DoesNotContain(icon, TrayIcon.GetIcons(app)!);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ShutdownFixture : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("convertdesk-shutdown-").FullName;
        public BlockingDocumentConverter Converter { get; }
        public JobQueue Queue { get; }
        public ConversionService Conversions { get; }
        public Preset Preset { get; } = new() { Id = "to-pdf", Name = "PDF", TargetFormat = "pdf" };
        public string Input { get; }

        public ShutdownFixture(bool cooperative)
        {
            Input = Path.Combine(_directory, "source.txt");
            File.WriteAllText(Input, "keep original");
            var store = new UserDataStore(Path.Combine(_directory, "settings"));
            store.Load();
            Converter = new BlockingDocumentConverter(cooperative);
            Queue = new JobQueue(new ConverterCatalog([Converter]), enforceProductFormats: true);
            Conversions = new ConversionService(Queue, store, new ShutdownLocalizer(), new NullPlatformServices());
        }

        public async ValueTask DisposeAsync()
        {
            Converter.Release.TrySetResult();
            await Queue.DisposeAsync();
            Conversions.Dispose();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class BlockingDocumentConverter(bool cooperative) : IConverter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public string Id => "shutdown-test";
        public string DisplayName => "Shutdown test";
        public IReadOnlyList<ConversionEdge> Edges { get; } = [new("txt", "pdf")];
        public int MaxParallelism => 1;
        public EngineStatus GetStatus() => EngineStatus.Available();
        public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            if (cooperative)
            {
                try { await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
            }
            else
                await Release.Task.ConfigureAwait(false);
            return [];
        }
    }

    private sealed class ShutdownLocalizer : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key;
        public string Format(string key, params object[] args) => key;
        public event EventHandler? LanguageChanged { add { } remove { } }
    }
}
