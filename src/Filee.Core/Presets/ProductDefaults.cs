using Filee.Core.Profiles;

namespace Filee.Core.Presets;

/// <summary>Factory library for ConvertDesk. The generic catalog remains available for legacy migrations.</summary>
public static class ProductDefaults
{
    public static List<Preset> CreatePresets() =>
    [
        new() { Id = "to-pdf", Name = "PDF", TargetFormat = "pdf" },
        new() { Id = "to-docx", Name = "DOCX", TargetFormat = "docx" },
        new() { Id = "to-pptx", Name = "PPTX", TargetFormat = "pptx" },
        new() { Id = "to-html", Name = "HTML", TargetFormat = "html" },
        new() { Id = "to-txt", Name = "TXT", TargetFormat = "txt" },
    ];

    public static List<ToolbarProfile> CreateProfiles() =>
    [
        new()
        {
            Id = "documents", NameKey = "profile.office", IsFallback = true,
            Extensions = ["html", "htm", "xhtml", "pdf", "pptx", "docx", "txt"],
            PresetIds = ["to-pdf", "to-docx", "to-pptx", "to-html", "to-txt"],
            BackPresetIds = [],
        },
    ];
}
