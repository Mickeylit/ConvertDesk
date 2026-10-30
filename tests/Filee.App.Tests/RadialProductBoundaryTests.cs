using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class RadialProductBoundaryTests
{
    [AvaloniaTheory]
    [InlineData("photo.png")]
    [InlineData("script.py")]
    [InlineData("notes.md")]
    public void Unsupported_extensions_do_not_offer_a_conversion_even_when_an_engine_can_read_them(string file)
    {
        using var f = new Fixture();
        f.Model.Load([file], clickMode: true);

        Assert.False(Assert.Single(f.Model.Items).IsEnabled);
        Assert.Null(f.Model.PresetAt(0, -1));
        Assert.Contains("HTML, PDF, PPTX, DOCX, TXT", f.Model.CenterSubtitle);
        Assert.False(f.Model.CanFlip);
        f.Model.ShowHint(-1, -1, centerHot: false);
        Assert.Contains("HTML, PDF, PPTX, DOCX, TXT", f.Model.CenterSubtitle);
    }

    [AvaloniaFact]
    public void Mixed_supported_and_unsupported_drop_is_blocked_as_a_whole()
    {
        using var f = new Fixture();
        f.Model.Load(["notes.txt", "photo.png"], clickMode: false);

        Assert.False(Assert.Single(f.Model.Items).IsEnabled);
        Assert.Equal(2, f.Model.Files.Count);
        Assert.Null(f.Model.PresetAt(0, -1));
    }

    [AvaloniaFact]
    public void A_subsequent_supported_drop_clears_the_restriction()
    {
        using var f = new Fixture();
        f.Model.Load(["script.py"], clickMode: true);
        f.Model.Load(["notes.txt"], clickMode: true);

        Assert.True(Assert.Single(f.Model.Items).IsEnabled);
        Assert.Equal("to-pdf", f.Model.PresetAt(0, -1)?.Id);
        Assert.Equal("donut.close_hint", f.Model.CenterSubtitle);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("convertdesk-radial-").FullName;
        public RadialViewModel Model { get; }

        public Fixture()
        {
            var store = new UserDataStore(_directory);
            store.Load();
            store.Presets.Clear();
            store.Presets.Add(new() { Id = "to-pdf", TargetFormat = "pdf" });
            store.Profiles.Clear();
            store.Profiles.Add(new() { Id = "mixed", IsFallback = true, PresetIds = ["to-pdf"] });
            var loc = new KeyLocalizer();
            var catalog = new ConverterCatalog([new ReadableConverter()]);
            Model = new RadialViewModel(store, loc, new PresetAvailability(catalog, loc, []));
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class ReadableConverter : IConverter
    {
        public string Id => "test-readable";
        public string DisplayName => "Test readable formats";
        public IReadOnlyList<ConversionEdge> Edges { get; } =
            [new("png", "pdf"), new("txt", "pdf"), new("md", "pdf")];
        public int MaxParallelism => 1;
        public EngineStatus GetStatus() => EngineStatus.Available();
        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class KeyLocalizer : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key;
        public string Format(string key, params object[] args) => key;
        public event EventHandler? LanguageChanged { add { } remove { } }
    }
}
