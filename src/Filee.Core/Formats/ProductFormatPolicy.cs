using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.Core.Formats;

/// <summary>
/// The Studio product deliberately exposes a small, document-focused format set.
///
/// Internal conversion routes may still use other formats. Product entry points apply
/// this policy to inputs, selectable outputs, and saved toolbar settings.
/// </summary>
public static class ProductFormatPolicy
{
    /// <summary>Format ids that may be detected as input or selected as an output.</summary>
    public static IReadOnlySet<string> AllowedFormatIds { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "html", "pdf", "pptx", "docx", "txt" };

    /// <summary>File extensions accepted by the Studio (without a leading dot).</summary>
    public static IReadOnlySet<string> AllowedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "html", "htm", "xhtml", "pdf", "pptx", "docx", "txt",
        };

    /// <summary>Formats shown in user-facing pickers, with only supported extension aliases.</summary>
    public static IReadOnlyList<FileFormat> Formats => FormatRegistry.Known
        .Where(f => IsAllowedFormatId(f.Id))
        .Select(f => f with { Extensions = f.Extensions.Where(IsAllowedExtension).ToArray() })
        .ToList();

    public static bool IsAllowedFormatId(string? id) =>
        id is not null && AllowedFormatIds.Contains(id);

    public static bool IsAllowedExtension(string? extension) =>
        extension is not null && AllowedExtensions.Contains(extension.TrimStart('.'));

    public static bool IsAllowedPath(string path) =>
        IsAllowedExtension(FormatRegistry.ExtensionOf(path));

    /// <summary>Whether a preset may remain in the Studio library.</summary>
    public static bool IsAllowedPreset(Preset preset) =>
        !preset.IsTool && (preset.IsGroup || IsAllowedFormatId(preset.TargetFormat));

    /// <summary>Whether a profile can claim a source extension in the Studio.</summary>
    public static bool IsAllowedProfileExtension(string extension) =>
        IsAllowedExtension(extension);

    /// <summary>
    /// Removes unsupported extensions and preset references from a profile. The returned
    /// value reports whether the profile changed.
    /// </summary>
    public static bool SanitizeProfile(ToolbarProfile profile, ISet<string> allowedPresetIds)
    {
        var changed = false;
        var extensions = profile.Extensions
            .Where(IsAllowedProfileExtension)
            .Select(e => e.TrimStart('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!profile.Extensions.SequenceEqual(extensions, StringComparer.OrdinalIgnoreCase))
        {
            profile.Extensions = extensions;
            changed = true;
        }

        var ids = profile.PresetIds.Where(allowedPresetIds.Contains).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ToolbarProfile.MaxSlices).ToList();
        if (!profile.PresetIds.SequenceEqual(ids, StringComparer.OrdinalIgnoreCase))
        {
            profile.PresetIds = ids;
            changed = true;
        }

        var back = (profile.BackPresetIds ?? [])
            .Where(allowedPresetIds.Contains).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ToolbarProfile.MaxSlices).ToList();
        if (profile.BackPresetIds is null || !profile.BackPresetIds.SequenceEqual(back, StringComparer.OrdinalIgnoreCase))
        {
            profile.BackPresetIds = back;
            changed = true;
        }

        // A normal profile with no supported extensions can never be selected. Removing
        // it is handled by UserDataStore; fallback profiles are allowed to stay empty.
        return changed;
    }
}
