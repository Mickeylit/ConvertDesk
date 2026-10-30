using System.Globalization;
using System.Text;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Filee.Engines.Tests;

public sealed class PlainTextSlidePaginationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "convertdesk-text-slides", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Preparation_escapes_markup_and_preserves_blank_lines_and_indentation()
    {
        const string source = "\r\n# literal heading\r\n\r\n<b>& not a tag</b>\r\n  two  spaces\r\n<script>raw()</script>\r\n";
        var document = new HtmlParser().ParseDocument(PandocConverter.PlainTextSlidesHtml(source));
        Assert.Empty(document.QuerySelectorAll("script,b,h1,img"));
        Assert.Equal(source.Replace("\r\n", "\n"), Assert.Single(document.QuerySelectorAll("pre")).TextContent);
    }

    [Fact]
    public void Long_chinese_lines_and_joined_emoji_are_split_without_losing_text_elements()
    {
        var source = string.Concat(Enumerable.Repeat("中文段落👩‍💻e\u0301🇨🇳", 120));
        var pages = Pages(source);
        Assert.True(pages.Count > 3);
        Assert.Equal(source, string.Concat(pages).Replace("\n", ""));
        var expectedElements = Elements(source);
        var actualElements = pages.SelectMany(page => page.Split('\n')).SelectMany(Elements).ToList();
        Assert.Equal(expectedElements, actualElements);
        Assert.All(pages, page => Assert.InRange(page.Split('\n').Length, 1, PandocConverter.PlainTextSlideLines));
        Assert.All(pages.SelectMany(page => page.Split('\n')), line =>
            Assert.True(Elements(line).Count <= PandocConverter.PlainTextSlideColumns));
    }

    [Fact]
    public void English_words_stay_whole_when_they_fit_and_no_spaces_are_discarded()
    {
        var words = Enumerable.Range(0, 180).Select(i => $"token{i:D3}").ToArray();
        var source = string.Join(" ", words);
        var pages = Pages(source);
        Assert.True(pages.Count > 1);
        Assert.Equal(source, string.Concat(pages).Replace("\n", ""));
        foreach (var line in pages.SelectMany(page => page.Split('\n')))
            Assert.All(line.Split(' ', StringSplitOptions.RemoveEmptyEntries), token => Assert.Contains(token, words));
    }

    [Fact]
    public void Paragraph_boundaries_and_empty_lines_survive_page_boundaries()
    {
        var source = string.Join("\n\n", Enumerable.Range(0, 24).Select(i => $"Paragraph {i:D2}\nSecond line.")) + "\n";
        var pages = Pages(source);
        Assert.True(pages.Count > 1);
        Assert.Equal(source, string.Join("\n", pages));
        Assert.All(pages, page => Assert.InRange(page.Split('\n').Length, 1, PandocConverter.PlainTextSlideLines));
    }

    [Fact]
    public async Task Real_pandoc_preserves_literal_markup_and_blank_lines_as_editable_text()
    {
        const string source = "\n# Not a heading\n\n<b>literal & text</b>\n<script>alert('x')</script>\n  two  spaces\n最后一行";
        var output = await ConvertAsync(source);
        using var deck = PresentationDocument.Open(output, false);
        var slides = Slides(deck);
        var slide = Assert.Single(slides);
        Assert.Empty(slide.Slide!.Descendants<P.Picture>());
        Assert.Equal(source, SlideText(slide).Replace('\u00a0', ' '));
        Validate(deck);
    }

    [Fact]
    public async Task Real_pandoc_long_text_is_paged_conservatively_and_every_character_is_retained()
    {
        var source = string.Join("\n\n", Enumerable.Range(1, 48).Select(i =>
            $"第{i:D3}段：跨平台文档转换保留全部正文，中文长句也能合理分页。👩‍💻 " +
            "Literal <b>HTML</b> & # Markdown remain plain text. " + new string('W', 50)));
        var output = await ConvertAsync(source);
        using var deck = PresentationDocument.Open(output, false);
        var slides = Slides(deck);
        Assert.True(slides.Count > 10, $"Expected multiple readable pages, got {slides.Count}.");
        Assert.All(slides, slide =>
        {
            Assert.Empty(slide.Slide!.Descendants<P.Picture>());
            Assert.InRange(slide.Slide!.Descendants<A.Break>().Count(), 0, PandocConverter.PlainTextSlideLines - 1);
        });
        var text = string.Join("\n", slides.Select(SlideText));
        Assert.Equal(WithoutWhitespace(source), WithoutWhitespace(text));
        Validate(deck);
    }

    private static IReadOnlyList<string> Pages(string source) => new HtmlParser()
        .ParseDocument(PandocConverter.PlainTextSlidesHtml(source)).QuerySelectorAll("pre")
        .Select(pre => pre.TextContent).ToList();

    private static List<string> Elements(string text)
    {
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) result.Add(enumerator.GetTextElement());
        return result;
    }

    private async Task<string> ConvertAsync(string source)
    {
        var converter = new PandocConverter();
        Assert.True(converter.GetStatus().IsAvailable, "Provision the pinned Pandoc engine before running text-slide integration tests.");
        Directory.CreateDirectory(_directory);
        var input = Path.Combine(_directory, "source.txt");
        var output = Path.Combine(_directory, "result.pptx");
        await File.WriteAllTextAsync(input, source, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        var outputs = await converter.ConvertAsync(new ConversionStep(input, "txt", "pptx",
            new Preset { TargetFormat = "pptx" }, new Output(output), _directory), null, TestContext.Current.CancellationToken);
        Assert.Equal(output, Assert.Single(outputs));
        Assert.Equal(source, await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken));
        return output;
    }

    private static List<SlidePart> Slides(PresentationDocument deck) => deck.PresentationPart!.Presentation!.SlideIdList!
        .Elements<P.SlideId>().Select(id => (SlidePart)deck.PresentationPart!.GetPartById(id.RelationshipId!.Value!)).ToList();

    private static string SlideText(SlidePart slide) => string.Join("\n", slide.Slide!.Descendants<A.Paragraph>()
        .Select(paragraph => string.Concat(paragraph.Descendants().Select(element => element switch
        {
            A.Text text => text.Text,
            A.Break => "\n",
            _ => "",
        }))));

    private static string WithoutWhitespace(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static void Validate(PresentationDocument deck)
    {
        var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(deck).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => e.Description)));
    }

    private sealed class Output(string path) : IOutputAllocator
    {
        public string Allocate(string extension, string? suffix = null) => path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
