// Application start-up: builds services, applies settings, wires global gestures to the donut toolbar.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.Services.Documents;
using Filee.App.Services.Triggers;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filee.App;

public partial class App : Application
{
    private readonly List<string> _pendingConvertFiles = [];
    private IDisposable? _convertDebounce;
    private bool _selectionPending;

    /// <summary>Forwarded launches and empty selections can come in bursts: a few lines a minute are enough.</summary>
    private static readonly LogThrottle Throttle = new(TimeSpan.FromSeconds(30));

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        SingleSelectionGuard.Register();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppHost.Build(preloadedStore: Program.PreloadedStore);
            Start(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Ends Filee as the tray's Quit does.</summary>
    public static void Quit() => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var log = AppHost.Get<ILogger<App>>();
        // macOS: "Convert with Filee" in Finder's right-click menu. Registered first, so a service picked while
        // Filee wasn't running is delivered as soon as it starts. A request without files Filee can find (another
        // file manager put its selection on the pasteboard in a form Filee doesn't read) is logged with what it held.
        if (OperatingSystem.IsMacOS())
            Filee.Platform.MacOS.MacOSServicesProvider.Register(
                files => Dispatcher.UIThread.Post(() => HandleCommandLine(CommandLine.Parse(["--convert", .. files]))),
                types => log.LogWarning("Convert with ConvertDesk was picked, but the request named no existing file; it held: {Types}",
                    types.Count == 0 ? "nothing" : string.Join(", ", types)));
        // Exceptions in UI event handlers end Filee; write them down first (they are not marked handled).
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            log.LogCritical(e.Exception, "Unhandled exception on the UI thread");
        var watchdog = new UiWatchdog(action => Dispatcher.UIThread.Post(action), AppHost.Get<ILogger<UiWatchdog>>());
        watchdog.Start();
        var store = AppHost.Get<UserDataStore>();
        if (Program.PreloadedStore is null)
            store.Load();

        var loc = AppHost.Get<LocalizationService>();
        loc.Attach(this);
        ApplySettings(store.Settings);
        // Window zoom: set once here, not with the other settings, since the zoom saves itself (UiZoom) without
        // raising SettingsChanged.
        UiZoom.Configure(store.Settings.UiScale, scale =>
        {
            try
            {
                store.SaveUiScale(scale);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Could not remember the window zoom");
            }
        });
        // Every engine's availability, checked once off the UI thread: the first donut or drop otherwise starts
        // every engine's check (FFmpeg -version, folder searches) on the UI thread and freezes it meanwhile.
        _ = AppHost.Get<ConverterCatalog>().CheckInBackgroundAsync();

        // Global gestures → donut toolbar
        var triggers = AppHost.Get<TriggerService>();
        var radial = AppHost.Get<RadialController>();
        var platform = AppHost.Get<IPlatformServices>();
        triggers.DragStarted += (_, e) => radial.ShowForDrag(e.X, e.Y);
        triggers.DragEnded += (_, _) => radial.OnDragGestureEnded();
        triggers.SelectionGesture += async (_, e) =>
        {
            if (_selectionPending || store.Settings.Paused)
                return;
            _selectionPending = true;
            try
            {
                var files = OperatingSystem.IsMacOS()
                    ? await Task.Run(platform.GetFileManagerSelection) : platform.GetFileManagerSelection();
                if (files.Count > 0 && !store.Settings.Paused)
                    radial.ShowForFiles(e.X, e.Y, files);
                else if (files.Count == 0 && Throttle.TryEnter("empty-selection", out var held))
                    log.LogInformation("The selection shortcut found no selected files in {Window}{Elevation}{Held}",
                        platform.DescribeForegroundWindow() is { Length: > 0 } window ? window : "the foreground window",
                        Program.Elevation.ShellCannotReachFilee ? " (Filee runs as administrator and can't read Explorer)" : "",
                        LogThrottle.HeldText(held));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogWarning(ex, "Could not read the file manager selection");
                AppHost.Get<WindowService>().ShowMain("triggers");
                if (AppHost.Get<WindowService>().Main is { } window)
                    _ = AppHost.Get<WindowService>().MessageAsync(window, ex.Message);
            }
            finally
            {
                _selectionPending = false;
            }
        };
        triggers.Start();
        log.LogInformation("ConvertDesk {Version} started", ProductInfo.Version);
        // Which way the transparent windows (donut, toast) are drawn, for reports of one that isn't see-through (#269).
        if (OperatingSystem.IsWindows())
        {
            log.LogInformation("{Rendering}", WindowsRendering.Describe(Environment.GetEnvironmentVariable(WindowsRendering.Variable)));
            TransparentWindows.Watch(log);
        }
        // System-wide gestures are optional. Permissions are requested only from the explicit settings action.
        store.SettingsChanged += (_, _) => ApplySettings(store.Settings);

        AppHost.Get<TrayService>().Create(this);
        AppHost.Get<WindowService>().EnsureToast();
        // Likewise the work folders of conversions that never got to clean up after themselves.
        _ = Task.Run(() => JobQueue.DeleteStaleWorkFolders(JobQueue.DefaultWorkRoot, TimeSpan.FromDays(1)));
        // Once LibreOffice is installed: prepare its profiles while the user isn't converting yet.
        AppHost.Get<EngineDownloadService>().WarmUpInBackground(TimeSpan.FromSeconds(10));

        // Later launches (context menu, Send To, double-clicking the exe) forward their arguments here.
        if (Program.Instance is { } instance)
            ListenForLaterLaunches(instance, log);
        Program.InstanceAcquired += again => ListenForLaterLaunches(again, log);

        desktop.Exit += (_, _) =>
        {
            _convertDebounce?.Dispose();
            _pendingConvertFiles.Clear();
            UiZoom.Flush();
            watchdog.Dispose();
            AppHost.Get<MemoryTrimmer>().Dispose();
            AppHost.Get<TriggerService>().Dispose();
            AppHost.Get<TrayService>().Dispose();
            DocumentShutdown.Stop(AppHost.Get<ConversionService>(), AppHost.Get<JobQueue>(), log: log);
        };

        // This independent build does not show the upstream release slideshow.
        HandleCommandLine(Program.Options, firstLaunch: true);
        if (OperatingSystem.IsMacOS())
            this.TryGetFeature<IActivatableLifetime>()?.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Clicking the Dock icon or opening Filee again in Finder brings the window back, as Mac apps do
                        // (#232), also while it only runs in the background.
                        AppHost.Get<WindowService>().ShowMain(store.Settings.FirstRunCompleted ? null : "home");
                        if (!store.Settings.ShowInDock && !platform.TrySetShowInDock(false))
                            log.LogWarning("Could not restore the macOS Dock visibility policy after reopening Filee");
                    });
            };
        // Filee runs as administrator while Explorer doesn't (an installer started "as administrator" started it):
        // say so once, next to the tray, and on the Home page for as long as it lasts.
        if (AppHost.Get<ElevationService>().ShellCannotReachFilee)
            DispatcherTimer.RunOnce(() => AppHost.Get<WindowService>().ShowElevationNotice(), TimeSpan.FromSeconds(2));
    }

    private void ListenForLaterLaunches(SingleInstance instance, ILogger log)
    {
        instance.ArgumentsReceived += args =>
        {
            if (Throttle.TryEnter("forwarded", out var held))
                log.LogInformation("A later launch handed over {Count} argument(s){Held}", args.Length, LogThrottle.HeldText(held));
            Dispatcher.UIThread.Post(() => HandleCommandLine(CommandLine.Parse(args), forwarded: true));
        };
        instance.StartListening();
    }

    /// <summary>Applies everything derived from settings. Called at start-up and whenever settings are saved.</summary>
    private static void ApplySettings(AppSettings settings)
    {
        // Before the catalog is created (the first time) or re-checked: engines look for the user's own copies first.
        var ownCopiesChanged = !EngineEnvironment.OwnCopies.OrderBy(p => p.Key).SequenceEqual(settings.EngineOwnCopies.OrderBy(p => p.Key));
        if (ownCopiesChanged)
            EngineEnvironment.OwnCopies = new Dictionary<string, string>(settings.EngineOwnCopies);
        var routesChanged = EngineEnvironment.LibreOfficeForSlides != settings.LibreOfficeForSlides
                            || EngineEnvironment.LibreOfficeForHwp != settings.LibreOfficeForHwp
                            || EngineEnvironment.LibreOfficeForDocuments != settings.LibreOfficeForDocuments
                            || EngineEnvironment.LibreOfficeForSheets != settings.LibreOfficeForSheets;
        EngineEnvironment.LibreOfficeForSlides = settings.LibreOfficeForSlides;
        EngineEnvironment.LibreOfficeForHwp = settings.LibreOfficeForHwp;
        EngineEnvironment.LibreOfficeForDocuments = settings.LibreOfficeForDocuments;
        EngineEnvironment.LibreOfficeForSheets = settings.LibreOfficeForSheets;

        var loc = AppHost.Get<LocalizationService>();
        if (loc.NeedsUpdate(settings.Language))
            loc.SetLanguage(settings.Language);

        var custom = CustomTheme.Load(AppHost.Get<UserDataStore>().Directory, AppHost.Get<ILogger<App>>());
        AppHost.Get<ThemeService>().Apply(settings.Theme, loc.Language, custom);
        AppHost.Get<TriggerService>().Apply(settings);
        AppHost.Get<ConverterCatalog>().Priority = settings.EnginePriority;
        if (ownCopiesChanged || routesChanged)
        {
            AppHost.Get<ConverterCatalog>().Refresh();
            AppHost.Get<EngineDownloadService>().RefreshStatus();
        }
        AppHost.Get<SystemIntegrationService>().Apply(settings);
    }

    private void HandleCommandLine(CommandLine options, bool firstLaunch = false, bool forwarded = false)
    {
        // Pairs with "A later launch handed over …": the request reached the UI thread too.
        if (forwarded && Throttle.TryEnter("forwarded-handled", out var held))
            AppHost.Get<ILogger<App>>().LogInformation("Handling a later launch: {Request}{Held}",
                options.Quit ? "quit" : options.ConvertFiles.Count > 0 ? $"convert {options.ConvertFiles.Count} file(s)" : "show ConvertDesk",
                LogThrottle.HeldText(held));
        if (options.Quit)
        {
            // The installer is about to replace or remove Filee's files (Program.RunInstallerCommand waits for this).
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            return;
        }
        // The Windows 11 Explorer menu hands over huge selections in a temp file; its paths are in ConvertFiles now.
        options.DeleteListFile();
        if (options.ConvertFiles.Count > 0)
        {
            // Explorer may start one process per selected file: collect them for a moment, then open once.
            _pendingConvertFiles.AddRange(options.ConvertFiles);
            _convertDebounce?.Dispose();
            _convertDebounce = DispatcherTimer.RunOnce(OpenPendingFiles, TimeSpan.FromMilliseconds(350));
            return;
        }

        var store = AppHost.Get<UserDataStore>();
        ApplyInstallerChoices(options, store);
        if (!options.Background || options.ShowSettings || !firstLaunch)
            AppHost.Get<WindowService>().ShowMain(store.Settings.FirstRunCompleted ? null : "home");

        if (options.InstallEngines is { } engines)
        {
            // Picked on the installer's engine page: download them now. Picking none answers the first-run question.
            if (engines.Count > 0)
                AppHost.Get<WindowService>().ShowEngineSetup(engines);
        }
        if (!store.Settings.FirstRunCompleted)
        {
            store.Settings.FirstRunCompleted = true;
            store.SaveSettings();
        }
    }

    /// <summary>
    /// Options ticked in Setup, and the language picked there. Saving applies them (SystemIntegrationService, the
    /// language) as if they had been switched on the General page.
    /// </summary>
    private static void ApplyInstallerChoices(CommandLine options, UserDataStore store)
    {
        // Only Filee's languages; anything else keeps the setting (normally "auto", Windows' language).
        var language = LocalizationService.Languages.FirstOrDefault(l => l.Code.Equals(options.Language, StringComparison.OrdinalIgnoreCase)).Code;
        if (options.StartWithWindows is null && options.ContextMenu is null && language is null)
            return;
        if (options.StartWithWindows is { } start)
            store.Settings.StartWithSystem = start;
        if (options.ContextMenu is { } menu)
            store.Settings.ContextMenuEnabled = menu;
        if (language is not null)
            store.Settings.Language = language;
        store.SaveSettings();
    }

    private void OpenPendingFiles()
    {
        var files = _pendingConvertFiles.Distinct(FileSystemPaths.Comparer).ToList();
        _pendingConvertFiles.Clear();
        var (x, y) = AppHost.Get<TriggerService>().LastCursor;
        if (x == 0 && y == 0 && AppHost.Get<WindowService>().Main is { } main)
            (x, y) = (main.Position.X + 200, main.Position.Y + 200);
        AppHost.Get<RadialController>().ShowForFiles(x, y, files, forAnotherProcess: true);
    }

}
