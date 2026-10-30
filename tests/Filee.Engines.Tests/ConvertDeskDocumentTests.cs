// Real conversion coverage for the product's five public formats. No full media engine registry is used.
using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Pdf;

namespace Filee.Engines.Tests;

public sealed class ConvertDeskDocumentTests : IDisposable
{
    private const string Marker = "ConvertDeskProbe314159";
    private static readonly string[] Formats = ["html", "pdf", "pptx", "docx", "txt"];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "convertdesk-document-tests", Guid.NewGuid().ToString("N"));

    public static TheoryData<string, string> AllConversions
    {
        get
        {
            var pairs = new TheoryData<string, string>();
            foreach (var source in Formats)
                foreach (var target in Formats.Where(target => target != source))
                    pairs.Add(source, target);
            return pairs;
        }
    }

    public static TheoryData<string, string> BuiltInTextConversions
    {
        get
        {
            var pairs = new TheoryData<string, string>();
            foreach (var source in Formats)
                foreach (var target in new[] { "html", "txt" }.Where(target => target != source))
                    pairs.Add(source, target);
            return pairs;
        }
    }

    [Theory]
    [MemberData(nameof(AllConversions))]
    public async Task Every_document_pair_converts_to_a_valid_file_and_keeps_the_original(string source, string target)
    {
        var converters = EngineRegistry.CreateDocumentConverters(new EngineEnvironment(Path.Combine(_folder, "data")));
        var catalog = new ConverterCatalog(converters) { Priority = new AppSettings().EnginePriority };
        await RunAndValidateAsync(source, target, catalog);
    }

    [Theory]
    [MemberData(nameof(AllConversions))]
    public async Task Every_document_pair_also_works_with_only_the_packaged_engines(string source, string target)
    {
        Assert.NotNull(EngineEnvironment.BundledProgram("rhwp", "rhwp.exe"));
        Assert.NotNull(EngineEnvironment.BundledProgram("pandoc", "pandoc.exe"));
        // Codex's tool PATH supplies LibreOffice. The shipped app contains rhwp/Pandoc only, so verify that exact
        // engine set separately without changing any global search flags or depending on tools from this host.
        string[] optional = ["libreoffice", "ghostscript", "tesseract", "vision"];
        var converters = EngineRegistry.CreateDocumentConverters(new EngineEnvironment(Path.Combine(_folder, "data")))
            .Where(converter => !optional.Contains(converter.Id));
        var catalog = new ConverterCatalog(converters) { Priority = new AppSettings().EnginePriority };
        await RunAndValidateAsync(source, target, catalog);
    }

    [Theory]
    [MemberData(nameof(BuiltInTextConversions))]
    public async Task Html_and_text_exports_work_without_external_programs(string source, string target)
    {
        // This catalog contains only the two in-process exporters. No process-based engine can be selected.
        var catalog = new ConverterCatalog([new DocumentExportConverter(), new PdfTextConverter()]);
        await RunAndValidateAsync(source, target, catalog);
    }

    [Fact]
    public async Task Powerpoint_pdf_with_the_bundled_renderer_keeps_the_source_text()
    {
        // The matrix follows the product's preferred route, which can be LibreOffice. Exercise rhwp separately.
        var converters = EngineRegistry.CreateDocumentConverters(new EngineEnvironment(Path.Combine(_folder, "data")))
            .Where(converter => converter.Id != "libreoffice");
        await RunAndValidateAsync("pptx", "pdf", new ConverterCatalog(converters));
    }

    [Fact]
    public void Pdf_numeric_clusters_use_actual_text_instead_of_counting_painted_glyphs()
    {
        Directory.CreateDirectory(_folder);
        const string prefix = "ConvertDeskProbe314";
        const double x = 72, y = 100, advance = 6;
        var clusterX = x + prefix.Length * advance;
        var pdf = new GlyphPdfBuilder().Run(prefix, x, y, advance);
        // LibreOffice's Linux Libertine Graphite shaping may paint a spacer + numeral, both mapping to '1'.
        // The PDF explicitly declares that this cluster represents ONE digit. It must not become '11'.
        pdf.ActualText("1", glyphs => glyphs.Glyph("1", clusterX, y, 1.5).Glyph("1", clusterX + 1.5, y, 4.5));
        pdf.Run("59", clusterX + advance, y, advance);
        var path = pdf.Save(Path.Combine(_folder, "numeric-cluster.pdf"));
        using (var raw = UglyToad.PdfPig.PdfDocument.Open(path))
            Assert.Equal("ConvertDeskProbe3141159", raw.GetPage(1).Text);
        Assert.Equal(Marker, PdfDocumentReader.PlainText(PdfDocumentReader.ReadText(path)).Trim());
    }

    private async Task RunAndValidateAsync(string source, string target, ConverterCatalog catalog)
    {
        Directory.CreateDirectory(_folder);
        var input = MakeSource(source);
        var original = SHA256.HashData(await File.ReadAllBytesAsync(input, TestContext.Current.CancellationToken));
        var route = catalog.CreatePlanner().Plan(source, target);
        Assert.True(route is not null,
            $"No {source} → {target} route. Provision the pinned rhwp and Pandoc document engines before running this integration suite. " +
            string.Join("; ", catalog.All.Select(converter => $"{converter.Id}: {catalog.StatusOf(converter).IsAvailable}")));
        var outputDirectory = Path.Combine(_folder, "results");
        var preset = new Preset
        {
            Name = "ConvertDesk integration test",
            TargetFormat = target,
            Output = new OutputRule { Location = OutputLocation.CustomFolder, CustomFolder = outputDirectory },
        };
        var job = new ConversionJob([input], preset, preset.Name);
        using var cancellation = TestContext.Current.CancellationToken.Register(job.Cancel);
        await using var queue = new JobQueue(catalog, EngineRegistry.FindPdfMerger(catalog.All), enforceProductFormats: true);
        await queue.RunAsync(job);
        Assert.True(job.State == JobState.Completed,
            $"{source} → {target}: " + string.Join("; ", job.Files.Select(file => $"{file.ErrorKey}: {file.ErrorDetail}")));
        var output = Assert.Single(job.Outputs);
        Assert.Equal("." + target, Path.GetExtension(output));
        Assert.NotEqual(input, output);
        Assert.True(File.Exists(output) && new FileInfo(output).Length > 0, "Output must be a nonempty file.");
        ValidateResult(output, target);
        Assert.Equal(original, SHA256.HashData(await File.ReadAllBytesAsync(input, TestContext.Current.CancellationToken)));
    }

    private string MakeSource(string format)
    {
        var path = Path.Combine(_folder, "source." + format);
        switch (format)
        {
            case "txt":
                File.WriteAllText(path, Marker + "\n\nSecond paragraph with plain text.", new UTF8Encoding(false));
                break;
            case "html":
                File.WriteAllText(path, $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Document probe</title></head><body><h1>{Marker}</h1><p>Second paragraph with <strong>formatted</strong> text.</p></body></html>", new UTF8Encoding(false));
                break;
            case "docx":
                new DocxBuilder().Paragraph(DocxBuilder.P(DocxBuilder.R(Marker)))
                    .Paragraph(DocxBuilder.P(DocxBuilder.R("Second paragraph with document text."))).Save(path);
                break;
            case "pptx":
                var deck = new PptxBuilder();
                deck.Slide(deck.Title(Marker) + deck.Body(("Second paragraph with slide text.", 0))).Save(path);
                break;
            case "pdf":
                var pdf = new PdfBuilder().NewPage();
                pdf.Line(Marker, 18, pdf.Font(), 40, 60)
                    .Line("Second paragraph with PDF text.", 11, pdf.Font(), 40, 100).Save(path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
        return path;
    }

    private static void ValidateResult(string path, string format)
    {
        switch (format)
        {
            case "docx":
                using (var word = WordprocessingDocument.Open(path, false))
                {
                    ValidateOpenXml(word);
                    Assert.Contains(Marker, word.MainDocumentPart!.Document!.InnerText);
                }
                break;
            case "pptx":
                using (var deck = PresentationDocument.Open(path, false))
                {
                    ValidateOpenXml(deck);
                    Assert.NotNull(deck.PresentationPart?.Presentation?.SlideIdList);
                    Assert.NotEmpty(deck.PresentationPart!.SlideParts);
                    // PDF slides deliberately contain images. Their speaker notes preserve searchable source text.
                    var text = string.Join("\n", deck.PresentationPart.SlideParts.Select(slide =>
                        slide.Slide!.InnerText + "\n" + slide.NotesSlidePart?.NotesSlide?.InnerText));
                    Assert.Contains(Marker, text);
                }
                break;
            case "pdf":
                using (var pdf = UglyToad.PdfPig.PdfDocument.Open(path))
                {
                    Assert.True(pdf.NumberOfPages > 0);
                    // Page.Text is glyph-level text: it duplicates composite glyphs whose /ActualText is one
                    // character (LibreOffice Graphite numerals). Use the PDF's semantic text, retaining the exact
                    // expected marker rather than normalizing repeated digits or dropping this assertion.
                    Assert.Contains(Marker, PdfDocumentReader.PlainText(PdfDocumentReader.ReadText(path)));
                }
                break;
            case "html":
                var html = File.ReadAllText(path);
                Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("</html>", html, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(Marker, html);
                break;
            case "txt":
                Assert.Contains(Marker, File.ReadAllText(path));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static void ValidateOpenXml(OpenXmlPackage package)
    {
        var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(package)
            .Select(error => $"{error.Part?.Uri} {error.Path?.XPath}: {error.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
