namespace Filee.Cli.Tests;

public class ProductFormatBoundaryTests
{
    [Theory]
    [InlineData("html", "html")]
    [InlineData(".HTM", "html")]
    [InlineData("xhtml", "html")]
    [InlineData("pdf", "pdf")]
    [InlineData("pptx", "pptx")]
    [InlineData("docx", "docx")]
    [InlineData("txt", "txt")]
    [InlineData("png", null)]
    [InlineData("md", null)]
    [InlineData("py", null)]
    [InlineData("hwpx", null)]
    [InlineData("zip", null)]
    public void Format_argument_only_exposes_document_formats(string argument, string? expected) =>
        Assert.Equal(expected, Cli.FindFormat(argument)?.Id);

    [Fact]
    public void Folder_input_expansion_ignores_other_formats()
    {
        var directory = Directory.CreateTempSubdirectory("convertdesk-inputs-").FullName;
        try
        {
            foreach (var name in new[] { "page.html", "report.docx", "photo.png", "video.mp4", "script.py" })
                File.WriteAllText(Path.Combine(directory, name), "sample");
            var missing = new List<string>();
            var files = Cli.ExpandInputs([directory], recursive: false, missing);
            Assert.Empty(missing);
            Assert.Equal(["page.html", "report.docx"], files.Select(path => Path.GetFileName(path)!).Order().ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
