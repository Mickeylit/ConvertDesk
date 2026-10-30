using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.App.Services.Documents;

public sealed record DocumentInputSelection(IReadOnlyList<string> Accepted, IReadOnlyList<string> Rejected);
public sealed record DocumentFileAssessment(string Path, string? FormatId, PresetJudgement Result);

/// <summary>Every input remains represented, including the files that prevent this batch from running.</summary>
public sealed record DocumentBatchAssessment(string Target, bool TargetSupported, IReadOnlyList<DocumentFileAssessment> Files)
{
    public int ReadyCount => Files.Count(f => f.Result.Use == PresetUse.Usable);
    public bool CanConvert => TargetSupported && Files.Count > 0 && ReadyCount == Files.Count;
    public bool NeedsPackage => Files.Any(f => f.Result.Use == PresetUse.NeedsPackage);
}

public sealed record DocumentConversionRequest(IReadOnlyList<string> Files, Preset Preset);
public sealed record DocumentConversionPreparation(DocumentBatchAssessment Assessment, DocumentConversionRequest? Request);

/// <summary>
/// The document workspace's application boundary: accepts only the five product formats, checks every source,
/// and creates a request only for a wholly convertible batch. It never starts a job or changes a source file.
/// Engine route decisions are shared by source/target format until the catalog changes; file existence is not cached.
/// </summary>
public sealed class DocumentConversionPlanner : IDisposable
{
    private sealed record Source(string Path, FileFormat? Format, string? Error);
    private readonly ILocalizer _loc;
    private readonly PresetAvailability? _availability;
    private readonly ConverterCatalog? _catalog;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, PresetAvailability.PaletteSession> _sessions = [];
    private readonly Dictionary<(string Source, string Target), PresetJudgement> _capabilities = [];

    public DocumentConversionPlanner(ILocalizer loc, PresetAvailability? availability, ConverterCatalog? catalog)
    {
        _loc = loc;
        _availability = availability;
        _catalog = catalog;
        if (catalog is not null)
            catalog.Changed += InvalidateCapabilities;
        loc.LanguageChanged += InvalidateCapabilities;
    }

    private string Text(string chinese, string english) =>
        _loc.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english;

    public static bool SupportsPath(string path) => ProductFormatPolicy.IsAllowedPath(path);

    /// <summary>Normalizes and deduplicates valid local documents, reporting every rejected input to the caller.</summary>
    public DocumentInputSelection SelectInputs(IEnumerable<string> paths)
    {
        var accepted = new List<string>();
        var rejected = new List<string>();
        var seen = new HashSet<string>(FileSystemPaths.Comparer);
        foreach (var path in paths)
        {
            var source = Inspect(path);
            if (source.Error is not null)
                rejected.Add(path);
            else if (seen.Add(source.Path))
                accepted.Add(source.Path);
        }
        return new(accepted.AsReadOnly(), rejected.AsReadOnly());
    }

    /// <summary>Checks each path once, then shares format-level route decisions across files and target choices.</summary>
    public IReadOnlyDictionary<string, DocumentBatchAssessment> AssessTargets(IEnumerable<string> paths, IEnumerable<string> targets)
    {
        var sources = paths.Select(Inspect).ToArray();
        var result = new Dictionary<string, DocumentBatchAssessment>(StringComparer.OrdinalIgnoreCase);
        lock (_cacheLock)
            foreach (var target in targets)
                result[target] = Assess(sources, target);
        return result;
    }

    /// <summary>Rechecks the live files at the explicit submit action; no partial request is ever returned.</summary>
    public DocumentConversionPreparation Prepare(IEnumerable<string> paths, string target)
    {
        var assessment = AssessTargets(paths, [target])[target];
        var request = assessment.CanConvert
            ? new DocumentConversionRequest(Array.AsReadOnly(assessment.Files.Select(f => f.Path).ToArray()), PresetFor(assessment.Target))
            : null;
        return new(assessment, request);
    }

    private Source Inspect(string path)
    {
        // The registry also maps scripts/logs to TXT. The stricter extension allowlist must run first.
        if (!SupportsPath(path))
            return new(path, null, Text("仅支持 HTML、PDF、PPTX、DOCX、TXT", "Only HTML, PDF, PPTX, DOCX and TXT are supported"));
        if (!File.Exists(path))
            return new(path, null, Text("文件已移动或删除", "The file was moved or deleted"));
        try
        {
            var fullPath = Path.GetFullPath(path);
            var format = FormatRegistry.Detect(fullPath);
            return format is not null && ProductFormatPolicy.IsAllowedFormatId(format.Id)
                ? new(fullPath, format, null)
                : new(fullPath, null, Text("无法识别此文档", "The document format is not recognized"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(path, null, Text("无法读取此文件路径", "The file path could not be read"));
        }
    }

    private DocumentBatchAssessment Assess(IReadOnlyList<Source> sources, string target)
    {
        var supported = ProductFormatPolicy.IsAllowedFormatId(target);
        var normalizedTarget = target.ToLowerInvariant();
        var files = sources.Select(source => new DocumentFileAssessment(source.Path, source.Format?.Id,
            !supported ? new(PresetUse.Unusable, Text("不支持此输出格式", "This output format is not supported"))
            : source.Error is not null ? new(PresetUse.Unusable, source.Error)
            : Judge(source.Format!, normalizedTarget))).ToArray();
        return new(normalizedTarget, supported, Array.AsReadOnly(files));
    }

    private PresetJudgement Judge(FileFormat format, string target)
    {
        if (format.Id == target)
            return new(PresetUse.Unusable, Text("已是该格式，无需转换", "Already in this format"));
        if (_availability is null)
            return new(PresetUse.Unusable, Text("转换引擎尚未就绪", "Conversion engines are not ready"));
        var key = (format.Id, target);
        if (_capabilities.TryGetValue(key, out var judgement))
            return judgement;
        if (!_sessions.TryGetValue(format.Id, out var session))
            _sessions[format.Id] = session = _availability.ForPalette([format]);
        return _capabilities[key] = session.Judge(PresetFor(target));
    }

    private static Preset PresetFor(string target) => new()
    {
        Id = "to-" + target,
        Name = target.ToUpperInvariant(),
        TargetFormat = target,
        // Default location is resolved by ConversionService at submit time. Safety is not a saved user preset.
        Output = new OutputRule { Conflict = ConflictPolicy.Rename, ReplaceOriginal = false },
    };

    private void InvalidateCapabilities(object? sender, EventArgs e)
    {
        lock (_cacheLock)
        {
            _sessions.Clear();
            _capabilities.Clear();
        }
    }

    public void Dispose()
    {
        if (_catalog is not null)
            _catalog.Changed -= InvalidateCapabilities;
        _loc.LanguageChanged -= InvalidateCapabilities;
    }
}
