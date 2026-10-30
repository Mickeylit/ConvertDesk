// A document workspace, optional gesture pause, and quit. The tray has no release-feed dependency.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Filee.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly WindowService _windows;
    private Application? _app;
    private TrayIcon? _tray;
    private NativeMenuItem? _open, _pause, _quit;

    [ActivatorUtilitiesConstructor]
    public TrayService(UserDataStore store, ILocalizer loc, IPlatformServices platform, WindowService windows)
        => (_store, _loc, _platform, _windows) = (store, loc, platform, windows);

    // Source compatibility for hosts which still construct the shared tray with an update service.
    public TrayService(UserDataStore store, ILocalizer loc, IPlatformServices platform, WindowService windows,
        UpdateService updates) : this(store, loc, platform, windows) { }

    public void Create(Application app)
    {
        Dispose();
        _app = app;
        using var stream = AssetLoader.Open(new Uri("avares://Filee/Assets/Icons/convertdesk.ico"));
        _open = new NativeMenuItem();
        _open.Click += OnOpen;
        _pause = new NativeMenuItem();
        _pause.Click += OnPause;
        _quit = new NativeMenuItem();
        _quit.Click += OnQuit;
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = ProductInfo.Name,
            Menu = new NativeMenu { Items = { _open, _pause, new NativeMenuItemSeparator(), _quit } },
            IsVisible = !_platform.SupportsDockVisibility || _store.Settings.ShowMenuBarIcon,
        };
        _tray.Clicked += OnOpen;
        TrayIcon.SetIcons(app, [_tray]);
        _loc.LanguageChanged += OnChanged;
        _store.SettingsChanged += OnChanged;
        Refresh();
    }

    private void OnOpen(object? sender, EventArgs e) => _windows.ShowMain("home");
    private void OnPause(object? sender, EventArgs e)
    {
        _store.Settings.Paused = !_store.Settings.Paused;
        _store.SaveSettings();
    }
    private void OnQuit(object? sender, EventArgs e) =>
        (_app?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_tray is null) return;
        _tray.IsVisible = !_platform.SupportsDockVisibility || _store.Settings.ShowMenuBarIcon;
        _open!.Header = _loc["tray.open"];
        _pause!.Header = _loc[_store.Settings.Paused ? "tray.resume" : "tray.pause"];
        _quit!.Header = _loc["tray.quit"];
        _tray.ToolTipText = _store.Settings.Paused ? _loc["triggers.paused"] : ProductInfo.Name;
    }

    public void Dispose()
    {
        _loc.LanguageChanged -= OnChanged;
        _store.SettingsChanged -= OnChanged;
        if (_tray is not null)
        {
            _tray.Clicked -= OnOpen;
            if (_app is not null) TrayIcon.GetIcons(_app)?.Remove(_tray);
            _tray.Dispose();
        }
        if (_open is not null) _open.Click -= OnOpen;
        if (_pause is not null) _pause.Click -= OnPause;
        if (_quit is not null) _quit.Click -= OnQuit;
        _tray = null;
        _app = null;
    }
}
