using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Office;

/// <summary>Direct document exports, avoiding lossy HWPX round trips for HTML and text.</summary>
public sealed class DocumentExportConverter : IConverter
{
    public string Id => "document-export";
    public string DisplayName => "Document export · HTML / TXT";
    public int MaxParallelism => 2;
    public IReadOnlyList<ConversionEdge> Edges { get; } =
        (from source in new[] { "html", "pdf", "pptx", "docx", "txt" }
         from target in new[] { "html", "txt" }
         where source != target && !(source == "pdf" && target == "txt")
         select new ConversionEdge(source, target, 8, DirectOnly: true)).ToList();

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.BuiltIn);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory,
            cancellationToken, ocrLayer: step.Preset.Pdf.Ocr);
        cancellationToken.ThrowIfCancellationRequested();
        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];
        progress?.Report(0.6);
        string content;
        if (step.To == "txt")
            content = PlainTextWriter.Write(document);
        else if (step.To == "html")
        {
            var images = Path.Combine(step.WorkDirectory, "html-images");
            Directory.CreateDirectory(images);
            var page = XhtmlWriter.Write(document, new XhtmlOptions
            {
                ImageSource = path => step.Preset.Document.LeaveOutPictures ? null : ImageData(path, images),
            }).Single();
            content = XhtmlWriter.Page(document.Title ?? Path.GetFileNameWithoutExtension(step.InputPath),
                page.Body, "und", null, epub: false);
        }
        else
            throw new NotSupportedException(step.To);
        await File.WriteAllTextAsync(output, content, new UTF8Encoding(false), cancellationToken);
        progress?.Report(1);
        return [output];
    }

    private static string? ImageData(string path, string folder) =>
        EbookFiles.CommonImage(path, folder, gifAndSvg: true) is ({ } file, { } mime)
            ? $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}"
            : null;
}
