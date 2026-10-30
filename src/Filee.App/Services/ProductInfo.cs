using System.Reflection;

namespace Filee.App.Services;

/// <summary>Product metadata is independent of any update or network service.</summary>
internal static class ProductInfo
{
    public const string Name = "ConvertDesk";
    public const string UpstreamUrl = "https://github.com/KnifeLemon/Filee";
    public static string Version { get; } = typeof(ProductInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ProductInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
