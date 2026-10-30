using System.Text.Json;
using System.Text.Json.Serialization;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ProductFormatPolicyTests
{
    [Theory]
    [InlineData("report.HTML", true)]
    [InlineData("report.htm", true)]
    [InlineData("report.xhtml", true)]
    [InlineData("report.pdf", true)]
    [InlineData("slides.pptx", true)]
    [InlineData("report.docx", true)]
    [InlineData("notes.txt", true)]
    [InlineData("report.doc", false)]
    [InlineData("notes.md", false)]
    [InlineData("script.py", false)]
    [InlineData("image.png", false)]
    public void Input_policy_accepts_only_document_extensions(string path, bool allowed) =>
        Assert.Equal(allowed, ProductFormatPolicy.IsAllowedPath(path));

    [Fact]
    public void A_new_install_and_library_reset_offer_five_conversions_in_one_document_profile()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        AssertDefaults();

        store.Presets.Add(new() { Id = "custom-pdf", TargetFormat = "pdf" });
        store.Profiles.Add(new() { Id = "custom", Extensions = ["pdf"], PresetIds = ["custom-pdf"] });
        store.ResetLibrary();
        AssertDefaults();

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Equal(store.Presets.Select(p => p.Id), reloaded.Presets.Select(p => p.Id));
        Assert.Single(reloaded.Profiles);

        void AssertDefaults()
        {
            Assert.Equal(["pdf", "docx", "pptx", "html", "txt"], store.Presets.Select(p => p.TargetFormat));
            var profile = Assert.Single(store.Profiles);
            Assert.Equal("documents", profile.Id);
            Assert.True(profile.IsFallback);
            Assert.Equal(store.Presets.Select(p => p.Id), profile.PresetIds);
            Assert.Empty(profile.BackPresetIds!);
            AssertLibrary(store);
        }
    }

    [Fact]
    public void Resetting_the_document_profile_restores_only_its_five_conversion_presets()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        var profile = Assert.Single(store.Profiles);
        store.Presets.RemoveAll(p => p.Id == "to-pptx");
        profile.PresetIds = ["to-pdf"];
        store.SaveLibrary();

        Assert.True(store.ResetProfile(profile));
        Assert.Equal(5, store.Presets.Count);
        Assert.Equal(["to-pdf", "to-docx", "to-pptx", "to-html", "to-txt"], profile.PresetIds);
        Assert.Empty(profile.BackPresetIds!);
        AssertLibrary(store);
    }

    [Fact]
    public void Loading_a_legacy_library_removes_other_formats_and_persists_the_migration()
    {
        using var dir = new TempDir();
        Write(dir, "settings.json", new AppSettings());
        Write(dir, "presets.json", new List<Preset>
        {
            new() { Id = "pdf", Name = "My PDF", TargetFormat = "pdf", Output = { ZipResult = true } },
            new() { Id = "png", TargetFormat = "png" },
            new() { Id = "tool", Kind = PresetKind.Tool, TargetFormat = "pdf" },
            new() { Id = "group", Kind = PresetKind.Group, TargetFormat = Preset.GroupTarget, SubPresetIds = ["pdf", "png"] },
        });
        Write(dir, "profiles.json", new List<ToolbarProfile>
        {
            new() { Id = "images", Extensions = ["png"], PresetIds = ["png", "pdf"] },
            new() { Id = "documents", Extensions = ["docx", "doc", "html", "py"], PresetIds = ["pdf", "png", "group"], BackPresetIds = ["tool", "png"] },
        });

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.Equal(2, store.Presets.Count);
        Assert.Equal("My PDF", store.Presets[0].Name);
        Assert.False(store.Presets[0].Output.ZipResult);
        Assert.Equal(["pdf"], store.Presets.Single(p => p.IsGroup).SubPresetIds);
        Assert.DoesNotContain(store.Profiles, p => p.Id == "images");
        var documents = Assert.Single(store.Profiles, p => p.Id == "documents");
        Assert.Equal(["docx", "html"], documents.Extensions);
        Assert.Equal(["pdf", "group"], documents.PresetIds);
        Assert.Empty(documents.BackPresetIds!);
        Assert.Contains(store.Profiles, p => p.IsFallback);
        Assert.Equal(store.Profiles.Count, store.Profiles.Select(p => p.Id).Distinct().Count());

        var saved = JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(Path.Combine(dir.Path, "presets.json")), Options)!;
        Assert.Equal(2, saved.Count);
        Assert.Equal("pdf", saved[0].TargetFormat);
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Equal(2, reloaded.Presets.Count);
        Assert.Contains(reloaded.Profiles, p => p.IsFallback);
    }

    [Fact]
    public void Save_reset_and_import_cannot_reintroduce_unsupported_formats()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Presets.Add(new Preset { Id = "video", TargetFormat = "mp4" });
        store.Profiles.Add(new ToolbarProfile { Id = "video", Extensions = ["mp4"], PresetIds = ["video"] });
        store.SaveLibrary();
        AssertLibrary(store);

        store.ResetLibrary();
        AssertLibrary(store);

        var bundlePath = Path.Combine(dir.Path, "bundle.json");
        File.WriteAllText(bundlePath, """
            {"presets":[{"id":"image","targetFormat":"png"},{"id":"to-pdf","targetFormat":"png"}],
             "profiles":[{"id":"image","extensions":["png"],"presetIds":["image"]}]}
            """);
        Assert.Equal(0, store.Import(bundlePath));
        Assert.Equal("pdf", store.FindPreset("to-pdf")?.TargetFormat);
        AssertLibrary(store);
    }

    private static void AssertLibrary(UserDataStore store)
    {
        Assert.All(store.Presets, p => Assert.True(ProductFormatPolicy.IsAllowedPreset(p)));
        Assert.All(store.Profiles.SelectMany(p => p.Extensions), e => Assert.True(ProductFormatPolicy.IsAllowedExtension(e)));
        var ids = store.Presets.Select(p => p.Id).ToHashSet();
        Assert.All(store.Profiles.SelectMany(p => p.PresetIds.Concat(p.BackPresetIds ?? [])), id => Assert.Contains(id, ids));
        Assert.Contains(store.Profiles, p => p.IsFallback);
    }

    [Fact]
    public async Task Product_queue_rejects_other_input_and_output_formats_before_running_engines()
    {
        using var dir = new TempDir();
        var engine = new FakeConverter("fake", new ConversionEdge("png", "pdf"), new ConversionEdge("txt", "png"));
        await using var queue = new JobQueue(new ConverterCatalog([engine]), enforceProductFormats: true);
        var input = new ConversionJob([dir.File("photo.png")], new Preset { TargetFormat = "pdf" }, "PDF");
        await queue.RunAsync(input);
        Assert.Equal(JobState.Failed, input.State);
        Assert.Equal("error.unsupported_source", input.Files[0].ErrorKey);
        Assert.False(File.Exists(Path.Combine(dir.Path, "photo.pdf")));

        var output = new ConversionJob([dir.File("notes.txt")], new Preset { TargetFormat = "png" }, "PNG");
        await queue.RunAsync(output);
        Assert.Equal(JobState.Failed, output.State);
        Assert.False(File.Exists(Path.Combine(dir.Path, "notes.png")));
        Assert.Empty(engine.OriginalSources);

        var zip = new ConversionJob([dir.File("other.txt")], new Preset { TargetFormat = "pdf", Output = { ZipResult = true } }, "ZIP");
        await queue.RunAsync(zip);
        Assert.Equal(JobState.Failed, zip.State);
        Assert.Empty(zip.Outputs);
    }

    [Fact]
    public async Task Product_queue_allows_internal_formats_between_supported_endpoints()
    {
        using var dir = new TempDir();
        var writer = new FakeConverter("writer", new ConversionEdge("docx", "hwpx"));
        var renderer = new FakeConverter("renderer", new ConversionEdge("hwpx", "pdf"));
        await using var queue = new JobQueue(new ConverterCatalog([writer, renderer]), enforceProductFormats: true);
        var job = new ConversionJob([dir.File("report.docx")], new Preset { TargetFormat = "pdf" }, "PDF");
        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(File.Exists(Path.Combine(dir.Path, "report.pdf")));
        Assert.Single(writer.OriginalSources);
        Assert.Single(renderer.OriginalSources);
        Assert.DoesNotContain(Directory.EnumerateFiles(dir.Path), p => p.EndsWith(".hwpx", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static void Write<T>(TempDir dir, string name, T value) =>
        File.WriteAllText(Path.Combine(dir.Path, name), JsonSerializer.Serialize(value, Options));
}
