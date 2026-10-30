// ConvertDesk shell: an explicit document workspace and a small preferences menu.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Filee.App.ViewModels;

/// <summary>Navigation entry. <see cref="Title"/> follows the UI language.</summary>
public sealed partial class NavItem(string key, string titleKey, string iconKey, Type pageType) : ObservableObject
{
    public string Key { get; } = key;
    public string TitleKey { get; } = titleKey;
    public Geometry? Icon { get; } = Application.Current?.FindResource(iconKey) as Geometry;
    public Type PageType { get; } = pageType;

    [ObservableProperty] private string _title = "";

    public void Refresh(ILocalizer loc) => Title = loc[TitleKey];
}

/// <summary>
/// One per main window (<see cref="WindowService.ShowMain"/>); disposed with it, which drops its subscriptions on the
/// long-lived services and the current page.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILocalizer _loc;
    private bool _disposed;

    // The overload keeps existing callers source-compatible without retaining an update-service dependency.
    public MainWindowViewModel(IServiceProvider services, ILocalizer loc, UpdateService updates) : this(services, loc) { }

    [ActivatorUtilitiesConstructor]
    public MainWindowViewModel(IServiceProvider services, ILocalizer loc)
    {
        _services = services;
        _loc = loc;
        VersionText = loc.Format("about.version", ProductInfo.Version);
        Items =
        [
            new("home", "nav.home", "Icon.Home", typeof(HomePageViewModel)),
            new("general", "nav.general", "Icon.Settings", typeof(GeneralPageViewModel)),
            new("triggers", "nav.triggers", "Icon.Keyboard", typeof(TriggersPageViewModel)),
            new("theme", "nav.theme", "Icon.Palette", typeof(ThemePageViewModel)),
            new("engines", "nav.engines", "Icon.Engine", typeof(EnginesPageViewModel)),
            new("about", "nav.about", "Icon.Info", typeof(DocumentAboutPageViewModel)),
        ];
        foreach (var item in Items)
            item.Refresh(loc);
        loc.LanguageChanged += OnLanguageChanged;
        _selectedItem = Items[0];
        _currentPage = CreatePage(Items[0]);
    }

    public IReadOnlyList<NavItem> Items { get; }

    public bool IsHome => SelectedItem.Key == "home";
    public string BackText => _loc.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "← 返回" : "← Back";
    public string SettingsText => _loc.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "设置" : "Settings";

    [ObservableProperty] private bool _settingsOpen;
    [RelayCommand] private void ToggleSettings() => SettingsOpen = !SettingsOpen;
    [RelayCommand] private void GoHome() { SettingsOpen = false; Navigate("home"); }
    [RelayCommand] private void NavigatePage(string key) { SettingsOpen = false; Navigate(key); }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsHome))] private NavItem _selectedItem;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageScroll))]
    private ObservableObject _currentPage;

    /// <summary>
    /// Pages scroll as a whole, except pages that fit the window height and scroll their own parts
    /// (<see cref="IFitsWindowHeight"/>, e.g. the presets list and editor).
    /// </summary>
    public ScrollBarVisibility PageScroll => CurrentPage is IFitsWindowHeight ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    [ObservableProperty] private string _versionText = "";
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var item in Items)
            item.Refresh(_loc);
        VersionText = _loc.Format("about.version", ProductInfo.Version);
        OnPropertyChanged(nameof(BackText));
        OnPropertyChanged(nameof(SettingsText));
        CurrentPage = CreatePage(SelectedItem); // rebuild so code-generated labels update too
    }

    public void Navigate(string key)
    {
        var item = Items.FirstOrDefault(i => i.Key == key);
        if (!_disposed && item is not null)
        {
            SettingsOpen = false;
            SelectedItem = item;
        }
    }

    // Pages are recreated on every visit so they always reflect the latest settings. A navigation list that loses its
    // selection keeps the open page (Choices.Restore).
    partial void OnSelectedItemChanged(NavItem? oldValue, NavItem newValue)
    {
        if (!Choices.Restore(oldValue, newValue, v => SelectedItem = v))
            CurrentPage = CreatePage(newValue);
    }

    // Old pages may hold event subscriptions on long-lived services.
    partial void OnCurrentPageChanged(ObservableObject? oldValue, ObservableObject newValue) => (oldValue as IDisposable)?.Dispose();

    private ObservableObject CreatePage(NavItem item) =>
        (ObservableObject)ActivatorUtilities.CreateInstance(_services, item.PageType);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loc.LanguageChanged -= OnLanguageChanged;
        (CurrentPage as IDisposable)?.Dispose();
    }
}
