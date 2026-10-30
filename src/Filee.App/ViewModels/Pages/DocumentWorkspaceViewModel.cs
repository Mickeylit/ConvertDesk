using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.App.Services.Documents;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

/// <summary>A document is staged first; choosing a format never starts a conversion.</summary>
public sealed partial class DocumentInputViewModel(string path, string removeText, Action<DocumentInputViewModel> remove) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public string RemoveText { get; } = removeText;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBlocked;
    [RelayCommand] private void Remove() => remove(this);
}

public sealed partial class DocumentTargetViewModel(string id, string description) : ObservableObject
{
    public string Id { get; } = id;
    public string Name => Id.ToUpperInvariant();
    public string Description { get; } = description;
    [ObservableProperty] private string _availability = "";
    [ObservableProperty] private bool _isUnavailable;
}

/// <summary>
/// The explicit add → choose → convert workflow. Availability is checked for every file, so a mixed batch never
/// silently drops unsupported inputs. ConversionService owns jobs beyond the lifetime of this page.
/// </summary>
public sealed partial class DocumentWorkspaceViewModel : ObservableObject, IDisposable
{
    // Page navigation recreates HomePage. Keep its unsent draft for this user-data session, without writing file
    // names to another settings file or retaining data stores after tests/profiles release them.
    private sealed class Draft
    {
        public List<string> Paths { get; set; } = [];
        public string Target { get; set; } = "pdf";
    }
    private static readonly ConditionalWeakTable<UserDataStore, Draft> Drafts = new();
    private readonly Draft _draft;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly ConversionService? _conversions;
    private readonly DocumentConversionPlanner _planner;
    private readonly ConverterCatalog? _catalog;
    private bool _disposed;
    private bool _starting;

    public DocumentWorkspaceViewModel(ILocalizer loc, UserDataStore store, ConversionService? conversions,
        PresetAvailability? availability, ConverterCatalog? catalog)
    {
        _loc = loc;
        _store = store;
        _conversions = conversions;
        _planner = new DocumentConversionPlanner(loc, availability, catalog);
        _catalog = catalog;
        _draft = Drafts.GetValue(store, _ => new Draft());
        Targets =
        [
            new("html", Text("网页文档", "Web document")),
            new("pdf", Text("固定版式", "Fixed layout")),
            new("pptx", Text("演示文稿", "Presentation")),
            new("docx", Text("可编辑文档", "Editable document")),
            new("txt", Text("纯文本", "Plain text")),
        ];
        _selectedTarget = Targets.FirstOrDefault(t => t.Id == _draft.Target) ?? Targets[1];
        foreach (var path in _planner.SelectInputs(_draft.Paths).Accepted)
            Files.Add(new DocumentInputViewModel(path, RemoveText, RemoveFile));
        if (conversions is not null)
        {
            foreach (var job in conversions.Jobs)
                Jobs.Add(job);
            conversions.Jobs.CollectionChanged += OnJobsChanged;
        }
        if (catalog is not null)
            catalog.Changed += OnCatalogChanged;
        store.SettingsChanged += OnSettingsChanged;
        RefreshState();
    }

    // The workspace has two deliberately compact copy sets; existing shared controls use the app localizer.
    private string Text(string chinese, string english) => _loc.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english;
    public string Title => Text("转换文档", "Convert documents");
    public string FilesTitle => Text("添加文档", "Add documents");
    public string AddFilesText => Text("选择文件", "Choose files");
    public string ContinueAddingText => Text("拖入更多文档", "Drop more documents");
    public string DropTitle => Text("把文档拖到这里", "Drop documents here");
    public string DropDescription => Text("文件会先加入待转换列表；准备好后再开始。", "Files are staged first. Start when you are ready.");
    public string TargetTitle => Text("转换为", "Convert to");
    public string ClearText => Text("清空列表", "Clear list");
    public string RemoveText => Text("移除文件", "Remove file");
    public string RefreshText => Text("重新检查", "Check again");
    public string JobsTitle => Text("转换进度", "Conversion progress");
    public string LocalProcessingText => Text("本地处理 · 保留原文件 · 重名结果自动编号", "Processed locally · Originals kept · Existing names numbered");
    public string PermissionOptionalText => Text("窗口内拖放可直接使用。若要在 Finder 中用 Option 拖动唤出快捷菜单，可开启辅助功能权限。", "Drop files into this window directly. Accessibility permission enables the Option-drag shortcut in Finder.");
    public string PermissionLinkText => Text("启用 Finder 拖放快捷键", "Enable the Finder drag shortcut");
    public string DependencyHint => Text("需要额外引擎时，请在“转换引擎”中安装或指定路径，然后重新检查。", "If an engine is required, install it or choose its path in Engines, then check again.");
    public string BlockedHint => Text("请更换输出格式，或移除下面标记为不可转换的文件。", "Choose another output format or remove the files marked as unavailable below.");
    public string PptxHint => Text("PDF → PPTX：每页生成一张图片幻灯片，页内文字不可编辑。HTML、DOCX、TXT → PPTX：按内容结构生成可编辑幻灯片，版式可能重新排列。", "PDF → PPTX: one image slide per page, with no editable page text. HTML, DOCX and TXT → PPTX: editable slides based on content structure; layout may change.");
    public string PickerFailureText => Text("无法打开文件选择器，请尝试把本地文档拖到窗口中。", "The file picker could not open. Try dropping local documents into the window.");
    public string BrowserDropText => Text("请先将文件保存到电脑，再添加到这里；此工作台只接收本地文档。", "Save the file to your computer first. This workspace accepts local documents.");
    public ObservableCollection<DocumentInputViewModel> Files { get; } = [];
    public IReadOnlyList<DocumentTargetViewModel> Targets { get; }
    public ObservableCollection<JobViewModel> Jobs { get; } = [];
    public bool HasFiles => Files.Count > 0;
    public bool HasJobs => Jobs.Count > 0;
    public bool HasContent => HasFiles || HasJobs;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasBlockedFiles => Files.Any(f => f.IsBlocked);
    public bool ShowsPptxHint => SelectedTarget.Id == "pptx";
    public bool CanConvert => !_starting && _conversions is not null && HasFiles && !HasBlockedFiles;
    public string FilesSummary => Text($"已添加 {Files.Count} 个文档", $"{Files.Count} document(s) added");
    public string ConvertText => HasFiles
        ? Text($"开始转换 · {Files.Count}", $"Convert · {Files.Count}")
        : Text("开始转换", "Convert");
    public string OutputHint => _store.Settings.DefaultOutput.Location switch
    {
        OutputLocation.CustomFolder => Text("保存到：", "Save to: ") + _store.Settings.DefaultOutput.CustomFolder,
        OutputLocation.Subfolder => Text("保存到原文件旁的子文件夹：", "Save to a subfolder beside each original: ") + _store.Settings.DefaultOutput.SubfolderName,
        _ => Text("保存到原文件所在文件夹", "Save beside each original"),
    };

    [ObservableProperty] private DocumentTargetViewModel _selectedTarget;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMessage))] private string _message = "";

    partial void OnSelectedTargetChanged(DocumentTargetViewModel? oldValue, DocumentTargetViewModel newValue)
    {
        if (newValue is null)
        {
            SelectedTarget = oldValue ?? Targets[1];
            return;
        }
        RefreshState();
    }

    public static bool SupportsPath(string path) => DocumentConversionPlanner.SupportsPath(path);

    public void AddFiles(IEnumerable<string> paths)
    {
        var selection = _planner.SelectInputs(paths);
        foreach (var path in selection.Accepted)
        {
            if (!Files.Any(f => Filee.Core.Platform.FileSystemPaths.Comparer.Equals(f.Path, path)))
                Files.Add(new DocumentInputViewModel(path, RemoveText, RemoveFile));
        }
        Message = selection.Rejected.Count == 0 ? "" : Text("未添加（仅支持本地 HTML、PDF、PPTX、DOCX、TXT 文件）：", "Not added (only local HTML, PDF, PPTX, DOCX and TXT files are supported): ")
            + string.Join(", ", selection.Rejected.Take(5).Select(System.IO.Path.GetFileName)) + (selection.Rejected.Count > 5 ? " …" : "");
        RefreshState();
    }

    public void ReportMessage(string message) => Message = message;

    private void RemoveFile(DocumentInputViewModel file)
    {
        Files.Remove(file);
        RefreshState();
    }

    [RelayCommand]
    private void ClearFiles()
    {
        Files.Clear();
        Message = "";
        RefreshState();
    }

    [RelayCommand]
    private void Refresh()
    {
        _catalog?.Refresh();
        RefreshState();
    }

    private void RefreshState()
    {
        if (_disposed)
            return;
        var assessments = _planner.AssessTargets(Files.Select(f => f.Path), Targets.Select(t => t.Id));
        foreach (var target in Targets)
        {
            var assessment = assessments[target.Id];
            if (target == SelectedTarget)
            {
                for (var i = 0; i < Files.Count; i++)
                {
                    var result = assessment.Files[i].Result;
                    Files[i].IsBlocked = result.Use != PresetUse.Usable;
                    Files[i].Status = result.Reason ?? Text("准备就绪", "Ready");
                }
                NeedsPackage = assessment.NeedsPackage;
            }
            target.Availability = Files.Count == 0 ? Text("选择此格式", "Select format")
                : assessment.CanConvert ? Text("全部可转换", "All files ready")
                : Text($"{assessment.ReadyCount}/{Files.Count} 可转换", $"{assessment.ReadyCount}/{Files.Count} ready");
            target.IsUnavailable = Files.Count > 0 && assessment.ReadyCount == 0;
        }
        _draft.Paths = Files.Select(f => f.Path).ToList();
        _draft.Target = SelectedTarget.Id;
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(HasBlockedFiles));
        OnPropertyChanged(nameof(FilesSummary));
        OnPropertyChanged(nameof(ConvertText));
        OnPropertyChanged(nameof(ShowsPptxHint));
        OnPropertyChanged(nameof(OutputHint));
        OnPropertyChanged(nameof(CanConvert));
        ConvertCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty] private bool _needsPackage;

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private void Convert()
    {
        if (_starting || _conversions is null)
            return;
        var preparation = _planner.Prepare(Files.Select(f => f.Path), SelectedTarget.Id);
        if (preparation.Request is not { } request)
        {
            RefreshState();
            return;
        }
        _starting = true;
        OnPropertyChanged(nameof(CanConvert));
        ConvertCommand.NotifyCanExecuteChanged();
        try
        {
            var job = _conversions.Start(request.Files, request.Preset, showInToast: false);
            if (job is not null)
            {
                Files.Clear();
                Message = "";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message = Text("无法开始转换：", "Could not start conversion: ") + ex.Message;
        }
        finally
        {
            _starting = false;
            RefreshState();
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RefreshState);
    private void OnSettingsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RefreshState);

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Keep completed job cards until the page is left, even after the toast service expires them.
        if (e.NewItems is not null)
            foreach (JobViewModel job in e.NewItems)
                if (!Jobs.Contains(job))
                    Jobs.Insert(0, job);
        while (Jobs.Count > 12 && Jobs.LastOrDefault(j => !j.IsRunning) is { } completed)
            Jobs.Remove(completed);
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(HasContent));
    }

    public void Dispose()
    {
        _disposed = true;
        _planner.Dispose();
        if (_conversions is not null)
        {
            foreach (var job in Jobs.Where(j => j.IsRunning))
                _conversions.ShowInToast(job);
            _conversions.Jobs.CollectionChanged -= OnJobsChanged;
        }
        if (_catalog is not null)
            _catalog.Changed -= OnCatalogChanged;
        _store.SettingsChanged -= OnSettingsChanged;
    }
}
