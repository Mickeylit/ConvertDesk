using Filee.App.Services;
using Filee.App.Services.Documents;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;

namespace Filee.App.Tests;

public sealed class DocumentConversionPlannerTests
{
    [Fact]
    public void Selection_accepts_only_the_five_document_types_and_html_aliases()
    {
        using var f = new Fixture();
        var accepted = new[] { "html", "HTM", "xhtml", "pdf", "pptx", "docx", "txt" }.Select(ext => f.Write("source." + ext)).ToArray();
        var rejected = new[] { "py", "log", "png", "doc", "ppt", "md" }.Select(ext => f.Write("source." + ext)).ToArray();
        var missing = Path.Combine(f.Directory, "missing.txt");
        var alias = Path.Combine(f.Directory, ".", "source.txt");
        var selection = f.Planner.SelectInputs([.. accepted, .. rejected, f.Directory, missing, alias]);

        Assert.Equal(accepted, selection.Accepted);
        Assert.Equal([.. rejected, f.Directory, missing], selection.Rejected);
        Assert.Equal("txt", FormatRegistry.Detect(rejected[0])!.Id); // The broader engine alias cannot bypass the boundary.
        Assert.Equal(0, f.Converter.EdgeReads);
    }

    [Fact]
    public void A_mixed_batch_keeps_every_failure_visible_and_never_returns_a_partial_request()
    {
        using var f = new Fixture();
        var text = f.Write("source.txt");
        var pdf = f.Write("already.pdf");
        var missing = Path.Combine(f.Directory, "missing.docx");
        var script = f.Write("script.py");
        var preparation = f.Planner.Prepare([text, pdf, missing, script], "pdf");

        Assert.Null(preparation.Request);
        Assert.Equal(new[] { text, pdf, missing, script }, preparation.Assessment.Files.Select(file => file.Path));
        Assert.Equal(1, preparation.Assessment.ReadyCount);
        Assert.Contains("Already", preparation.Assessment.Files[1].Result.Reason);
        Assert.Contains("moved or deleted", preparation.Assessment.Files[2].Result.Reason);
        Assert.Contains("Only HTML", preparation.Assessment.Files[3].Result.Reason);
    }

    [Fact]
    public void Unsupported_targets_and_empty_batches_cannot_create_a_request()
    {
        using var f = new Fixture();
        var input = f.Write("source.txt");
        foreach (var target in new[] { "png", "doc", "same", "" })
        {
            var preparation = f.Planner.Prepare([input], target);
            Assert.False(preparation.Assessment.TargetSupported);
            Assert.False(preparation.Assessment.CanConvert);
            Assert.Null(preparation.Request);
        }
        Assert.Null(f.Planner.Prepare([], "pdf").Request);
        Assert.Equal(0, f.Converter.EdgeReads);
    }

    [Fact]
    public void Requests_snapshot_all_inputs_and_apply_non_destructive_output_defaults()
    {
        using var f = new Fixture();
        var original = f.Write("source.txt", "keep original");
        var second = f.Write("second.txt");
        var existing = f.Write("source.pdf", "keep existing result");
        var inputs = new List<string> { original, second };
        var request = Assert.IsType<DocumentConversionRequest>(f.Planner.Prepare(inputs, "PDF").Request);
        inputs.Clear();

        Assert.Equal(new[] { original, second }, request.Files);
        Assert.Equal("pdf", request.Preset.TargetFormat);
        Assert.Equal(OutputLocation.Default, request.Preset.Output.Location);
        Assert.Equal(ConflictPolicy.Rename, request.Preset.Output.Conflict);
        Assert.False(request.Preset.Output.ReplaceOriginal);
        Assert.False(request.Preset.Output.AskEveryTime);
        Assert.Equal("keep original", File.ReadAllText(original));
        Assert.Equal("keep existing result", File.ReadAllText(existing));
        Assert.Equal(3, System.IO.Directory.GetFiles(f.Directory).Length);
    }

    [Fact]
    public void A_large_batch_builds_one_route_graph_per_source_format_and_reuses_it_across_target_changes()
    {
        using var f = new Fixture();
        var inputs = Enumerable.Range(0, 200).Select(i => f.Write($"source-{i}.txt")).Append(f.Write("web.html")).ToArray();
        var targets = new[] { "html", "pdf", "pptx", "docx", "txt" };
        var first = f.Planner.AssessTargets(inputs, targets);
        Assert.True(first["pdf"].CanConvert);
        Assert.Equal(2, f.Converter.EdgeReads);

        f.Planner.AssessTargets(inputs, targets.Reverse());
        Assert.NotNull(f.Planner.Prepare(inputs, "pdf").Request);
        Assert.Equal(2, f.Converter.EdgeReads);

        f.Catalog.Priority = [f.Converter.Id];
        f.Planner.AssessTargets(inputs, targets);
        Assert.Equal(4, f.Converter.EdgeReads);
    }

    [Fact]
    public void Cached_routes_do_not_cache_source_existence_at_submit_time()
    {
        using var f = new Fixture();
        var first = f.Write("first.txt");
        var second = f.Write("second.txt");
        Assert.NotNull(f.Planner.Prepare([first, second], "pdf").Request);
        File.Delete(second);
        var preparation = f.Planner.Prepare([first, second], "pdf");

        Assert.Null(preparation.Request);
        Assert.Equal(2, preparation.Assessment.Files.Count);
        Assert.Equal(1, preparation.Assessment.ReadyCount);
        Assert.Contains("moved or deleted", preparation.Assessment.Files[1].Result.Reason);
        Assert.Equal(1, f.Converter.EdgeReads);
    }

    [Fact]
    public void Catalog_refresh_rechecks_engine_availability_and_replaces_the_missing_package_result()
    {
        using var f = new Fixture(available: false);
        var input = f.Write("source.txt");
        var unavailable = f.Planner.Prepare([input], "pdf");
        Assert.Null(unavailable.Request);
        Assert.True(unavailable.Assessment.NeedsPackage);
        Assert.Contains("Test document engine", Assert.Single(unavailable.Assessment.Files).Result.Reason);

        f.Converter.Available = true;
        f.Catalog.Refresh();
        var available = f.Planner.Prepare([input], "pdf");
        Assert.NotNull(available.Request);
        Assert.False(available.Assessment.NeedsPackage);

        f.Converter.Available = false;
        f.Catalog.Refresh();
        Assert.Null(f.Planner.Prepare([input], "pdf").Request);
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("convertdesk-planner-").FullName;
        public CountingConverter Converter { get; }
        public ConverterCatalog Catalog { get; }
        public DocumentConversionPlanner Planner { get; }

        public Fixture(bool available = true)
        {
            Converter = new CountingConverter { Available = available };
            Catalog = new ConverterCatalog([Converter]);
            var loc = new Localizer();
            // Installed packages do not need hypothetical route graphs in the cache-counting test.
            var package = new EnginePackageState(new EnginePackage("test", [], 0, [Converter.Id]), loc)
            {
                Status = available ? EnginePackageStatus.Installed : EnginePackageStatus.NotInstalled,
            };
            Planner = new DocumentConversionPlanner(loc, new PresetAvailability(Catalog, loc, [package]), Catalog);
        }

        public string Write(string name, string contents = "document")
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            Planner.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class CountingConverter : IConverter
    {
        public bool Available { get; set; }
        public int EdgeReads { get; private set; }
        public string Id => "test-document-engine";
        public string DisplayName => "Test document engine";
        public IReadOnlyList<ConversionEdge> Edges
        {
            get
            {
                EdgeReads++;
                return [new("txt", "pdf"), new("txt", "html"), new("html", "pdf")];
            }
        }
        public int MaxParallelism => 1;
        public EngineStatus GetStatus() => Available ? EngineStatus.Available() : EngineStatus.Unavailable("missing");
        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Planning must not start a conversion.");
    }

    private sealed class Localizer : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key switch
        {
            "engines.package.test.name" => "Test document engine",
            "engine.reason.needs_package" => "Needs {0}",
            _ => key,
        };
        public string Format(string key, params object[] args) => string.Format(this[key], args);
        public event EventHandler? LanguageChanged { add { } remove { } }
    }
}
