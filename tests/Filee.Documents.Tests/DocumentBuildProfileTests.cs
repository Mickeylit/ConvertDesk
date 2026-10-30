using Filee.Engines;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;

namespace Filee.Documents.Tests;

public sealed class DocumentBuildProfileTests
{
    [Fact]
    public void Product_assembly_excludes_unrelated_converters_and_package_references()
    {
        var assembly = typeof(EngineRegistry).Assembly;
        string[] omittedTypes =
        [
            "Cad.CadConverter", "Models3D.ModelConverter", "Geo.GeoConverter", "Data.DataConverter",
            "Email.EmlConverter", "Ebooks.EbookConverter", "Ebooks.CalibreConverter", "Office.SpreadsheetConverter",
            "Media.FfmpegConverter", "Subtitles.SubtitleConverter", "Archives.ArchiveConverter", "Hwp.UnhwpConverter",
        ];
        foreach (var type in omittedTypes)
            Assert.Null(assembly.GetType("Filee.Engines." + type));
        string[] omittedAssemblies =
        [
            "ACadSharp", "SharpGLTF.Core", "MimeKitLite", "MimeKit", "MsgReader", "YamlDotNet", "Tomlyn",
            "Unhwp", "ExcelDataReader", "ExcelNumberFormat", "OpenMcdf", "RtfPipe", "UTF.Unknown",
        ];
        var referenced = assembly.GetReferencedAssemblies().Select(name => name.Name).ToList();
        foreach (var name in omittedAssemblies)
            Assert.DoesNotContain(name, referenced);
    }

    [Fact]
    public void Fallback_registry_and_readers_stay_inside_the_document_profile()
    {
        var environment = new EngineEnvironment(Path.Combine(Path.GetTempPath(), "convertdesk-profile-tests"));
        var expected = EngineRegistry.CreateDocumentConverters(environment).Select(converter => converter.Id);
        Assert.Equal(expected, EngineRegistry.CreateAll(environment).Select(converter => converter.Id));
        Assert.Equal(new[] { "docx", "html", "hwpx", "md", "pdf", "pptx", "txt" }, DocumentReaders.BuiltInFormats.Order());
        Assert.Empty(DocumentReaders.PandocFormats);
        Assert.False(VideoFrames.IsAvailable);
        Assert.Null(FfmpegTools.Locate());
    }
}
