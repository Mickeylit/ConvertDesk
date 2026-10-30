using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Services;
using Filee.Core.Localization;

namespace Filee.App.ViewModels.Pages;

/// <summary>Read-only product information. No updater, release slideshow or network subscriptions.</summary>
public sealed class DocumentAboutPageViewModel(ILocalizer loc) : ObservableObject
{
    private string Text(string chinese, string english) => loc.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english;
    public string Version => loc.Format("about.version", ProductInfo.Version);
    public string Description => Text("五种文档格式，一个简单入口。", "Five document formats, one simple workspace.");
    public string Privacy => Text("文档在本机处理，保留源文件。", "Documents are processed locally. Originals are preserved.");
    public string Attribution => Text(
        "基于 Myeonggu Kim 和 Filee contributors 的 MIT 开源代码开发。ConvertDesk 是独立衍生版本，保留原作者版权及第三方许可。",
        "An independent derivative of the MIT-licensed Filee project by Myeonggu Kim and Filee contributors. Original copyright and third-party licenses are retained.");
    public string LicenseTitle => Text("开源许可与来源", "Open-source licenses and attribution");
    public string OriginalLicense { get; } = Read("LICENSE");
    public string Notices { get; } = Read("THIRD-PARTY-NOTICES.md");
    public string RepositoryUrl => ProductInfo.UpstreamUrl;

    private static string Read(string name)
    {
        var path = new Uri("avares://Filee/Assets/" + name);
        using var reader = new StreamReader(AssetLoader.Open(path));
        return reader.ReadToEnd();
    }
}
